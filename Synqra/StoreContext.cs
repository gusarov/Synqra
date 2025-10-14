using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

using IStorage = IStorage<Event, Guid>;

public static class StoreContextExtensions
{
	public static bool IsOnline(this ISynqraStoreContext storeContext)
	{
		if (storeContext is StoreContext sc)
		{
			return sc.IsOnline;
		}
		return false;
	}
}

public class StrongReference
{
	public StrongReference(object target)
	{
		Target = target;
	}
	public StrongReference()
	{
	}
	public object Target { get; set; }
	public bool IsAlive => Target != null;
}

/// <summary>
/// StoreContext is a replayer, it is StateProcessor that also holds all processed objects in memory and reacts on any new events.
/// It can be used to replay events from scratch
/// It can also be treated like EF DataContext
/// </summary>
internal class StoreContext : ISynqraStoreContext, ICommandVisitor<CommandHandlerContext>, IEventVisitor<EventVisitorContext>
{
	static UTF8Encoding _utf8nobom = new UTF8Encoding(false, false);

	// Client could fetch a list of objects and keep it pretty much forever, it will be live and synced
	// Or client can fetch something just temporarily, like and then release it to free up memory and notification pressure

	internal readonly JsonSerializerOptions? _jsonSerializerOptions;
	private readonly IStorage _storage;
	private readonly EventReplicationService? _eventReplicationService;
	private readonly Dictionary<Guid, StoreCollection> _collections = new();
	private readonly ConcurrentDictionary<Guid, StrongReference> _attachedObjectsById = new();
	private readonly ConditionalWeakTable<object, AttachedObjectData> _attachedObjects = new();
	private readonly Dictionary<Type, TypeMetadata> _typeMetadataByType = new();
	private readonly Dictionary<Guid, TypeMetadata> _typeMetadataByTypeId = new();
	private byte _attachedMaintain;

	public bool IsOnline => _eventReplicationService?.IsOnline ?? false;

	static StoreContext()
	{
		AppContext.SetSwitch("Synqra.GuidExtensions.ValidateNamespaceId", false); // I use deterministic hash guids for named collections per type ids, and type id is also hash based by type name, so namespace id for collection is v5
	}

	public StoreContext(IStorage storage, EventReplicationService? eventReplicationService = null, JsonSerializerOptions? jsonSerializerOptions = null, JsonSerializerContext? jsonSerializerContext = null)
	{
		_storage = storage;
		_eventReplicationService = eventReplicationService;
		_jsonSerializerOptions = jsonSerializerOptions;
		if (jsonSerializerContext != null)
		{
			foreach (var supportedTypeData in jsonSerializerContext.GetType().GetCustomAttributesData().Where(x => x.AttributeType == typeof(JsonSerializableAttribute)))
			{
				var type = (Type)supportedTypeData.ConstructorArguments[0].Value;
				GetTypeMetadata(type);
			}
			if (jsonSerializerContext.Options.Converters.Count == 0)
			{
				throw new Exception("Something is wrong! We require JsonSerializerOptions to have converters registered!");
			}
		}
		else
		{
			throw new Exception("Something is wrong! We require JsonSerializerOptions to be registered!");
		}
	}

	internal AttachedObjectData Attach(object model, StoreCollection collection)
	{
		var data = GetAttachedData(model, default, collection, GetMode.RequiredNew);
		// data.Id
		return data;
	}

	internal (object? Model, AttachedObjectData? Attached) TryGetModel(Guid id)
	{
		if (_attachedObjectsById.TryGetValue(id, out var wr))
		{
			var model = wr.Target;
			if (model is not null && _attachedObjects.TryGetValue(model, out var attachedData) && attachedData is not null)
			{
				return (model, attachedData);
			}
			else
			{
				// clean up stale reference
				_attachedObjectsById.TryRemove(id, out _);
			}
		}
		return default;
	}

	internal bool TryGetModel(Guid id, out (object? Model, AttachedObjectData? Attached) data)
	{
		if (_attachedObjectsById.TryGetValue(id, out var wr))
		{
			var model = wr.Target;
			if (model is not null && _attachedObjects.TryGetValue(model, out var attachedData) && attachedData is not null)
			{
				data = (model, attachedData);
				return true;
			}
			else
			{
				// clean up stale reference
				_attachedObjectsById.TryRemove(id, out _);
			}
		}
		data = default;
		return false;
	}

	internal AttachedObjectData GetAttachedData(object model, Guid id, StoreCollection? collection, GetMode mode)
	{
		if (model == null)
		{
			throw new ArgumentNullException(nameof(model));
		}
#if NET8_0_OR_GREATER
		if (_attachedObjects.TryGetValue(model, out var attachedData) && attachedData is not null)
		{
			if (((byte)mode & 1) == 0)
			{
				throw new Exception("Object already have id assigned.");
			}
			attachedData.IsJustCreated = false;
			if (id != default && attachedData.Id != id)
			{
				throw new InvalidOperationException($"Object is already attached with different id <{attachedData.Id}>. Expected <{id}>.");
			}
			if (collection != default && attachedData.Collection != collection)
			{
				throw new InvalidOperationException($"Object is already attached with different collection <{collection}>. Expected <{collection}>.");
			}
			return attachedData;
		}
		else
		{
			switch ((byte)mode >> 1)
			{
				case 0:
					throw new InvalidOperationException($"Object is not attached to the store context.");
				case 1:
					return null!; // return null
				case 2:
					if (id == default)
					{
						id = GuidExtensions.CreateVersion7();
					}
					if (collection is null)
					{
						throw new Exception("Can not attach object without collection");
					}
					if (model is IBindableModel bm)
					{
						if (bm.Store != null)
						{
							if (bm.Store != this)
							{
								throw new Exception("The model is already attached to store. To Different store.");
							}
							else
							{
								throw new Exception("The model is already attached to store. It is same store but still, inconsistent.");
							}
						}
						bm.Store = this;
					}
					if (!_attachedObjectsById.TryAdd(id, new StrongReference(model)))
					{
						throw new Exception("This id is already used in the store. Pass default to generate new or make sure your id is fresh indeed");
					}
					if (++_attachedMaintain == 0)
					{
						// clean up weak references
						foreach (var key in _attachedObjectsById.Keys.ToArray())
						{
							if (_attachedObjectsById.TryGetValue(key, out var weakRef) && !weakRef.IsAlive)
							{
								_attachedObjects.Remove(key);
								_attachedObjectsById.Remove(key, out _);
							}
						}
					}
					if (_attachedObjects.TryAdd(model, attachedData = new AttachedObjectData
					{
						Id = id,
						IsJustCreated = true,
						Collection = collection,
					}))
					{
						_attachedObjectsById[id] = new StrongReference(model);
					};
					return attachedData;
				default:
					throw new IndexOutOfRangeException($"Unknown mode <{mode}>");
			}
		}
#else
		throw new Exception("Not implemented for older frameworks");
#endif
	}

	internal (bool IsJustCreated, Guid Id) GetOrCreateId(object model, StoreCollection? collection)
	{
#if NET8_0_OR_GREATER
		if (_attachedObjects.TryGetValue(model, out var attachedData) && attachedData is not null)
		{
			return (false, attachedData.Id);
		}
		else
		{
			if (collection is null)
			{
				throw new Exception("Can not attach object without collection");
			}
			if (_attachedObjects.TryAdd(model, attachedData = new AttachedObjectData
			{
				Id = GuidExtensions.CreateVersion7(),
				IsJustCreated = false,
				Collection = collection,
			}))
			{
				_attachedObjectsById[attachedData.Id] = new StrongReference(model);
			};
			return (true, attachedData.Id);
		}
#else
		throw new Exception("Not implemented for older frameworks");
#endif
	}

	public Guid GetId(object model)
	{
		return GetId(model, null, GetMode.RequiredId);
	}

	internal Guid GetId(object model, StoreCollection? collection, GetMode mode)
	{
#if NET8_0_OR_GREATER
		if (_attachedObjects.TryGetValue(model, out var attachedData) && attachedData is not null)
		{
			if (((byte)mode & 1) == 0)
			{
				throw new Exception("Object already have id assigned.");
			}
			return attachedData.Id;
		}
		else
		{
			switch ((byte)mode >> 1)
			{
				case 0:
					throw new InvalidOperationException($"Object is not attached to the store context.");
				case 1:
					return default; // return Guid.Empty
				case 2:
					if (_attachedObjects.TryAdd(model, attachedData = new AttachedObjectData
					{
						Id = GuidExtensions.CreateVersion7(),
						IsJustCreated = false,
						Collection = collection ?? throw new Exception("Collection is not specified for new object"),
					}))
					{
						_attachedObjectsById[attachedData.Id] = new StrongReference(model);
					};
					return attachedData.Id;
				default:
					throw new IndexOutOfRangeException($"Unknown mode <{mode}>");
			}
		}
		// throw new InvalidOperationException($"The object {model} is not attached to the store context.");
#else
		throw new Exception("Not implemented for older frameworks");
#endif
	}

	ISynqraCollection ISynqraStoreContext.GetCollection(Type type)
	{
		return (ISynqraCollection)GetCollectionInternal(type);
	}

	public StoreCollection GetCollection(Type type)
	{
		return GetCollectionInternal(type);
	}

	Guid GetCollectionId(Type rootType, string? name = null)
	{
		var typeId = GetTypeMetadata(rootType).TypeId;
		return GuidExtensions.CreateVersion5(typeId, name ?? "");
	}

	internal StoreCollection GetCollectionInternal(Type type, string? collectionName = null)
	{
		var collectionId = GetCollectionId(type, collectionName);
		var gtype = typeof(StoreCollection<>).MakeGenericType(type);
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_collections, collectionId, out var exists);
		if (!exists || slot == null)
		{
			slot = (StoreCollection)Activator.CreateInstance(gtype, [this
#if NET8_0_OR_GREATER
				, _jsonSerializerOptions
#endif
				, /*containerId*/ ContainerId
				, collectionId/*collectionId*/
				])!;
		}
		return slot;
#else
		throw new Exception("Not implemented for older frameworks");
#endif
	}

	public Guid ContainerId { get; }

	public ISynqraCollection<T> GetCollection<T>() where T : class
	{
		return (ISynqraCollection<T>)GetCollectionInternal(typeof(T));
	}

	public Task SubmitCommandAsync(ISynqraCommand newCommand)
	{
		if (newCommand is Command cmd)
		{
			if (cmd.CommandId == default)
			{
				cmd.CommandId = GuidExtensions.CreateVersion7();
			}
			if (cmd.ContainerId == default)
			{
				cmd.ContainerId = ContainerId;
			}
		}
		if (newCommand is SingleObjectCommand soc)
		{
			if (soc.Target != null)
			{
				var attached = GetAttachedData(soc.Target, default, null, GetMode.RequiredId);
				// TargetId
				if (soc.TargetId == default)
				{
					soc.TargetId = attached.Id;
				}
				else if (soc.TargetId != attached.Id)
				{
					throw new Exception("The target object has different id");
				}
				// CollectionId
				if (soc.CollectionId == default)
				{
					soc.CollectionId = attached.Collection.CollectionId;
				}
				else if (soc.CollectionId != attached.Collection.CollectionId)
				{
					throw new Exception("The target object Collection has different id");
				}
				// TargetTypeId
				var typeId = GetTypeMetadata(soc.Target.GetType()).TypeId; // TODO this might differ from root for hierarchy, do I need root here or a concrete type?
				if (soc.TargetTypeId == default)
				{
					soc.TargetTypeId = typeId;
				}
				else if (soc.TargetTypeId != typeId)
				{
					throw new Exception("The target object Type has different id");
				}
			}
		}

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
			throw new Exception("Only Syncra.Command can be an implementation of ICommand, please derive from Syncra.Command");
		}
		await cmd.AcceptAsync(this, commandHandlingContext);
		foreach (var @event in commandHandlingContext.Events)
		{
			await ProcessEventAsync(@event); // error handling - how to rollback state of entire model?
			await _storage.AppendAsync(@event); // store event in storage and trigger replication
		}
		_eventReplicationService?.Trigger(commandHandlingContext.Events);
	}

	/// <summary>
	/// Process and apply it locally
	/// </summary>
	private async Task ProcessEventAsync(Event newEvent)
	{
		await newEvent.AcceptAsync(this, null);
	}

	internal TypeMetadata GetTypeMetadata(Type type)
	{
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_typeMetadataByType, type, out var exists);
		if (!exists)
		{
			slot = new TypeMetadata
			{
				Type = type,
				TypeId = GuidExtensions.CreateVersion5(SynqraGuids.SynqraTypeNamespaceId, type.FullName), // it is not a secret, so for type identification SHA1 is totally fine
			};
			_typeMetadataByType[type] = slot;
			_typeMetadataByTypeId[slot.TypeId] = slot;
		}
		return slot;
#else
		if (!_typeMetadataByType.TryGetValue(type, out var metadata))
		{
			metadata = new TypeMetadata
			{
				Type = type,
				TypeId = GuidExtensions.CreateVersion5(SynqraGuids.SynqraTypeNamespaceId, type.FullName), // it is not a secret, so for type identification SHA1 is totally fine
			};
			_typeMetadataByType[type] = metadata;
			_typeMetadataByTypeId[metadata.TypeId] = metadata;
		}
		return metadata;
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
		var created = new CommandCreatedEvent
		{
			EventId = GuidExtensions.CreateVersion7(),
			Data = cmd,
			CommandId = cmd.CommandId,
			ContainerId = cmd.ContainerId,
		};
		ctx.Events.Add(created);
		/*
		var created = new ObjectCreatedEvent
		{
			EventId = Guid.CreateVersion7(),
			DataObject = cmd,
			CommandId = cmd.CommandId,
			TargetTypeId = GetTypeMetadata(),
			TargetId = cmd.CommandId,
		};
		ctx.Events.Add(created);
		*/
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
			ContainerId = cmd.ContainerId,
			EventId = GuidExtensions.CreateVersion7(),
			CollectionId = cmd.CollectionId,
			CommandId = cmd.CommandId,
			TargetTypeId = cmd.TargetTypeId,
			TargetId = cmd.TargetId,
			Data = cmd.Data,
			DataString = cmd.DataJson, // if json is cached here, let's use it to save on serialization
			DataObject = cmd.Target, // or may be entire object
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
			ContainerId = cmd.ContainerId,
			CommandId = cmd.CommandId,
			CollectionId = cmd.CollectionId,

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
		var collection = GetCollectionInternal(GetTypeMetadata(ev.TargetTypeId).Type);

		if (ev.TargetId == default)
		{
			throw new ArgumentException("TargetId required", nameof(ev));
		}

		object newItem;
		if (ev.DataObject != null)
		{
			newItem = ev.DataObject;
		}
		else if (TryGetModel(ev.TargetId, out var data))
		{
			newItem = data.Model;
		}
		else if (ev.DataString != null)
		{
			newItem = JsonSerializer.Deserialize(ev.DataString, typeMetadata.Type, _jsonSerializerOptions);
		}
		else if (ev.Data != null)
		{
			newItem = JsonSerializer.Deserialize(JsonSerializer.Serialize<IDictionary<string, object?>?>(ev.Data, _jsonSerializerOptions), typeMetadata.Type, _jsonSerializerOptions);
		}
		else
		{
			newItem = Activator.CreateInstance(typeMetadata.Type);
		}

		/*
		if (newItem is IIdentifiable<Guid> ig)
		{
			if (ig.Id == default)
			{
				throw new Exception($"Deserialized object's Id is not set.");
			}
			if (ig.Id != ev.TargetId)
			{
				throw new Exception($"Deserialized object's Id ({ig.Id}) does not match the expected TargetId ({ev.TargetId}).");
			}
		}
		*/

		var data2 = GetAttachedData(newItem, ev.TargetId, collection, GetMode.GetOrCreate);
		//if (!TryGetModel(ev.TargetId, out var data2))
		{
			
			// Attach(newItem, collection);
			// throw new Exception("The object is not attached yet");
		}
		// var dataX = Attach(newItem, collection);
		if (newItem is IBindableModel ibm)
		{
			if (ibm.Store == null)
			{
				ibm.Store = this;
			}
		}
		collection.AddByEvent(newItem);
		return Task.CompletedTask;
	}

	public Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx)
	{
		var tm = GetTypeMetadata(ev.TargetTypeId);
		var col = GetCollectionInternal(tm.Type);

		TryGetModel(ev.TargetId, out var data);
		if (data.Model is IBindableModel bm)
		{
			bm.Set(ev.PropertyName, ev.NewValue);
		}
		else if (data.Model is not null)
		{
			// throw new Exception($"The type '{data.Model.GetType().Name}' is not IBindableModel. Please add 'partial' keyword for generator to work.");
			data.Model.GetType().GetProperty(ev.PropertyName)?.SetValue(data.Model, ev.NewValue);
		}
		else
		{
			throw new Exception($"Cannot change property of unknown object {ev.TargetId}");
		}
		return Task.CompletedTask;
	}

	public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx)
	{
		return Task.CompletedTask;
		// throw new NotImplementedException("ObjectDeletedEvent is not implemented yet");
	}

	public Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx)
	{
		var commands = GetCollectionInternal(typeof(ISynqraCommand));
		commands.AddByEvent(ev.Data);
		return Task.CompletedTask;
		// throw new NotImplementedException("ObjectDeletedEvent is not implemented yet");
	}

	#endregion


}

internal class AttachedObjectData
{
	public required Guid Id { get; init; }
	public required StoreCollection Collection { get; init; }
	public required bool IsJustCreated { get; set; }
}

internal enum GetMode : byte
{
	// 0b_0000_0000
	//          MME
	// E - Behavior for existing object (0 - throw, 1 - return)
	// MM - Behavior for missing object (0 - throw, 1 - zero_default, 2 - create_id)

	// 0b_MM_E
	Invalid,     // 00 0
	RequiredId,  // 00 1
	MustAbsent,  // 01 0
	TryGet,      // 01 1
	RequiredNew, // 10 0
	GetOrCreate, // 10 1
}

internal static class SynqraStoreContextInternalExtensions
{
	internal static Guid GetId(this ISynqraStoreContext ctx, object model, StoreCollection? collection, GetMode mode)
	{
		return ((StoreContext)ctx).GetId(model, collection, mode);
	}

	internal static AttachedObjectData Attach(this ISynqraStoreContext ctx, object model, StoreCollection collection)
	{
		return ((StoreContext)ctx).Attach(model, collection);
	}

	internal static (bool IsJustCreated, Guid Id) GetOrCreateId(this ISynqraStoreContext ctx, object model, StoreCollection collection)
	{
		return ((StoreContext)ctx).GetOrCreateId(model, collection);
	}
}
