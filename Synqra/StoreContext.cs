using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

/// <summary>
/// StoreContext is a replayer, it is StateProcessor that also holds all processed objects in memory and reacts on any new events.
/// It can be used to replay events from scratch
/// It can also be treated like EF DataContext
/// </summary>
class StoreContext : ISynqraStoreContext, ICommandVisitor<CommandHandlerContext>, IEventVisitor<EventVisitorContext>
{
	// Client could fetch a list of objects and keep it pretty much forever, it will be live and synced
	// Or client can fetch something just temporarily, like and then release it to free up memory and notification pressure

	internal Dictionary<Type, IStoreCollectionInternal> _collections = new();

	public StoreContext(
		IStorage storage
#if NET8_0_OR_GREATER
		, JsonSerializerContext jsonSerializerContext
#endif
		)
	{
		_storage = storage;
#if NET8_0_OR_GREATER
		_jsonSerializerContext = jsonSerializerContext;
		foreach (var supportedTypeData in jsonSerializerContext.GetType().GetCustomAttributesData().Where(x=>x.AttributeType == typeof(JsonSerializableAttribute)))
		{
			var type = (Type)supportedTypeData.ConstructorArguments[0].Value;
			GetTypeMetadata(type);
		}
#endif
	}

	public IStoreCollection Get(Type type)
	{
		return GetInternal(type);
	}

	internal IStoreCollectionInternal GetInternal(Type type)
	{
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_collections, type, out var exists);
		if (!exists)
		{
			var gtype = typeof(StoreCollection<>).MakeGenericType(type);
			slot = Activator.CreateInstance(gtype, [this
#if NET8_0_OR_GREATER
				, _jsonSerializerContext
#endif
				]) as IStoreCollectionInternal;
		}
		return slot;
#else
		throw new NotSupportedException("Generic Get<T> is only supported in .NET 7 or greater");
#endif
	}

	public IStoreCollection<T> Get<T>() where T : class
	{
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_collections, typeof(T), out var exists);
		if (!exists)
		{
			slot = new StoreCollection<T>(this
#if NET8_0_OR_GREATER
				, _jsonSerializerContext
#endif
				);
		}
		return (IStoreCollection<T>)slot;
#else
		throw new NotSupportedException("Generic Get<T> is only supported in .NET 7 or greater");
#endif
	}

	public Task SubmitCommandAsync(ISynqraCommand newCommand)
	{
		return ProcessCommandAsync(newCommand);
	}

	public void SubmitCommand(Command newCommand)
	{
		ProcessCommandAsync(newCommand).GetAwaiter().GetResult();
	}

	/// <summary>
	/// Process and apply it locally
	/// </summary>
	private async Task ProcessCommandAsync(ISynqraCommand newCommand)
	{
		var commandHandlingContext = new CommandHandlerContext();
		if (newCommand is not Command cmd)
		{
			throw new Exception("Only internal Syncra.Command can be an implementation of ICommand");
		}
		await cmd.AcceptAsync(this, commandHandlingContext);
		foreach (var @event in commandHandlingContext.Events)
		{
			await ProcessEventAsync(@event); // error handling - how to rollback state of entire model?
			await _storage.AppendAsync(@event); // store event in storage
		}
	}

	/// <summary>
	/// Process and apply it locally
	/// </summary>
	private async Task ProcessEventAsync(Event newEvent)
	{
		await newEvent.AcceptAsync(this, null);
	}

	Dictionary<Type, TypeMetadata> _typeMetadataByType = new();
	Dictionary<Guid, TypeMetadata> _typeMetadataByTypeId = new();

#if NET8_0_OR_GREATER
	internal readonly JsonSerializerContext _jsonSerializerContext;
#endif
	private readonly IStorage _storage;

	static Guid _synqraTypeNamespaceId = new ("BAD8F923-FA74-4CA0-9AA3-70BB874ACC76");

	internal TypeMetadata GetTypeMetadata(Type type)
	{
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_typeMetadataByType, type, out var exists);
		if (!exists)
		{
			slot = new TypeMetadata
			{
				Type = type,
				TypeId = GuidExtensions.CreateVersion5(_synqraTypeNamespaceId, type.FullName), // it is not a secret, so for type identification SHA1 is totally fine
			};
			_typeMetadataByTypeId[slot.TypeId] = slot;
		}
		return slot;
#else
		throw new NotSupportedException("Type metadata is only supported in .NET 8 or greater");
#endif
	}

	internal TypeMetadata GetTypeMetadata(Guid typeId)
	{
		if (typeId == default)
		{
			throw new ArgumentException("typeId is empty", nameof(typeId));
		}
		if (_typeMetadataByTypeId.TryGetValue(typeId, out var metadata))
		{
			return metadata;
		}
		throw new Exception("Type id is unknown");
	}

	#region Command Handler

	public Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx)
	{
		return Task.CompletedTask;
	}

	public Task AfterVisitAsync(Command cmd, CommandHandlerContext ctx)
	{
		return Task.CompletedTask;
	}

	public Task VisitAsync(CreateObjectCommand cmd, CommandHandlerContext ctx)
	{
		var type = GetTypeMetadata(cmd.TargetTypeId);
		if (type is null)
		{
			throw new Exception("Unknown TypeId");
		}

		// ParentId is allowed for PostObject as speciaCase but is not allowed for ObjectCreated
		/*
		Id parentId = default;
		if (typeof(IHierarchy).IsAssignableFrom(type) || typeof(IMHierarchy).IsAssignableFrom(type)) // even though NH does not have field 'parentId' we can use same convention to allow specify first parent
		{
			var parentIdKey = nameof(IHierarchy.ParentId).ToCamel();
			if (data.TryGetValue(parentIdKey, out var parentIdValue))
			{
				parentId = parentIdValue is null ? default : Convert.ToString(parentIdValue).ToId();
				data.Remove(parentIdKey);
			}
		}
		*/

		var created = new ObjectCreatedEvent
		{
			EventId = GuidExtensions.CreateVersion7(),
			TargetTypeId = cmd.TargetTypeId,
			TargetId = cmd.TargetId,
			Data = cmd.Data,
			DataString	= cmd.DataJson, // if json is cached here, let's use it to save on serialization
			DataObject = cmd.DataObject, // or may be entire object
		};
		ctx.Events.Add(created);
		/*
		foreach (var kvp in data)
		{
			ctx.Events.Add(new ObjectPropertyChangedEvent
			{
				
			});
		}
		*/
		/*
		if (parentId != default)
		{
			ctx.Events.Add(new NodeMoved
			{
				Discriminator = cmd.Discriminator,
				UserId = cmd.UserId,
				TargetId = cmd.TargetId,
				NewValue = parentId,
			});
		}
		*/

		return Task.CompletedTask;
	}

	public Task VisitAsync(DeleteObjectCommand cmd, CommandHandlerContext ctx)
	{
		return Task.CompletedTask;
	}

	public Task VisitAsync(ChangeObjectPropertyCommand cmd, CommandHandlerContext ctx)
	{
		var created = new ObjectPropertyChangedEvent
		{
			EventId = GuidExtensions.CreateVersion7(),
			TargetTypeId = cmd.TargetTypeId,
			TargetId = cmd.TargetId,

			PropertyName = cmd.PropertyName,
			OldValue = cmd.OldValue,
			NewValue = cmd.NewValue,

			// Data = cmd.Data,
			// DataString = cmd.DataJson, // if json is cached here, let's use it to save on serialization
			// DataObject = cmd.DataObject, // or may be entire object
		};
		ctx.Events.Add(created);

		return Task.CompletedTask;
	}

	#endregion

	#region Event Handler

	public Task BeforeVisitAsync(Event ev, EventVisitorContext ctx)
	{
		if (ev.EventId == default)
		{
			throw new Exception("Event id is not set");
		}
		if (ev is SingleObjectEvent sev)
		{
			if (sev.TargetTypeId == default)
			{
				throw new Exception("TargetTypeId is not set");
			}
			if (sev.TargetId == default)
			{
				throw new Exception("TargetId is not set");
			}
		}
		return Task.CompletedTask;
	}

	public Task AfterVisitAsync(Event ev, EventVisitorContext ctx)
	{
		return Task.CompletedTask;
	}

	public Task VisitAsync(ObjectCreatedEvent ev, EventVisitorContext ctx)
	{
		var typeMetadata = GetTypeMetadata(ev.TargetTypeId);
		var collection = GetInternal(GetTypeMetadata(ev.TargetTypeId).Type);

		object newItem;
		if (ev.DataObject != null)
		{
			newItem = ev.DataObject;
		}
		else if (collection.TryGetAttached(ev.TargetId, out var attached))
		{
			newItem = attached;
		}
		else if (ev.DataString != null)
		{
			newItem = JsonSerializer.Deserialize(ev.DataString, typeMetadata.Type
#if NET8_0_OR_GREATER
				, _jsonSerializerContext.Options
#endif
				);
		}
		else if (ev.Data != null)
		{
			newItem = JsonSerializer.Deserialize(JsonSerializer.Serialize<IDictionary<string, object?>?>(ev.Data
#if NET8_0_OR_GREATER
				, _jsonSerializerContext.Options
#endif
				), typeMetadata.Type
#if NET8_0_OR_GREATER
				, _jsonSerializerContext.Options
#endif
				);
		}
		else
		{
			newItem = Activator.CreateInstance(typeMetadata.Type);
		}
		collection.AddByEvent(newItem);
		return Task.CompletedTask;
	}

	public Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx)
	{
		try
		{
			var tm = GetTypeMetadata(ev.TargetTypeId);
			var col = GetInternal(tm.Type);
			var so = new JsonSerializerOptions(
#if NET8_0_OR_GREATER
				_jsonSerializerContext.Options
#endif
				);
			var ti = so.GetTypeInfo(tm.Type);

#if DEBUG
			var so2 = new JsonSerializerOptions(
#if NET8_0_OR_GREATER
				_jsonSerializerContext.Options
#endif
				);
			var ti2 = so.GetTypeInfo(tm.Type);
			if (ReferenceEquals(ti2, ti))
			{
				throw new Exception($"Can't get isolated type info");
			}
#endif

			col.TryGetAttached(ev.TargetId, out var attached);
			if (!ReferenceEquals(attached, null))
			{
				ti.CreateObject = () => attached;
			}
			IDictionary<string, object> dic = new Dictionary<string, object>
			{
				[ev.PropertyName] = ev.NewValue,
			};
			var json = JsonSerializer.Serialize(dic
#if NET8_0_OR_GREATER
				, _jsonSerializerContext.Options
#endif
				);
			var patched = JsonSerializer.Deserialize(json, ti);
			if (!ReferenceEquals(attached, null))
			{
				if (!ReferenceEquals(patched, attached))
				{
					throw new Exception("Failed to patch existing object");
				}
			}
			return Task.CompletedTask;
		}
		catch (Exception ex)
		{
			throw;
			return Task.CompletedTask;
		}
	}

	public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx)
	{
		throw new NotImplementedException("ObjectDeletedEvent is not implemented yet");
	}

#endregion
}
