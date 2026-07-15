using Microsoft.Extensions.Logging;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using Synqra.Projection;
using Synqra.Projection.InMemory;
using System;
using System.Collections.Concurrent;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

using IAppendStorage = IAppendStorage<Event, Guid>;

public static class InMemoryStoreContextExtensions
{
	public static bool IsOnline(this IObjectStore storeContext)
	{
		if (storeContext is InMemoryProjection sc)
		{
			return sc.IsOnline;
		}
		return false;
	}
}

/// <summary>
/// StoreContext is a replayer, it is StateProcessor that also holds all processed objects in memory and reacts on any new events.
/// It can be used to replay events from scratch
/// It can also be treated like EF DataContext
/// </summary>
public class InMemoryProjection : IObjectStore, IProjection, ICommandVisitor<CommandHandlerContext>, IEventVisitor<EventVisitorContext>, ILinkIndex, IReplayProjection
{
	private static UTF8Encoding _utf8nobom = new UTF8Encoding(false, false);
	static InMemoryProjection()
	{
		AppContext.SetSwitch("Synqra.GuidExtensions.ValidateNamespaceIdHashChain", false); // I use deterministic hash guids for named collections per type ids, and type id is also hash based by type name, so namespace id for collection is v5
	}

	// Client could fetch a list of objects and keep it pretty much forever, it will be live and synced
	// Or client can fetch something just temporarily, like and then release it to free up memory and notification pressure

	internal readonly JsonSerializerOptions? _jsonSerializerOptions;

	private readonly IAppendStorage? _eventStorage;
	private readonly ISbxSerializerFactory _serializerFactory;
	public ITypeMetadataProvider TypeMetadataProvider { get; }

	private readonly IEventReplicationService? _eventReplicationService;
	private readonly IServiceProvider? _serviceProvider;
	private readonly Dictionary<Guid, InMemoryStoreCollection> _collections = new();
	private readonly ConcurrentDictionary<Guid, StrongReference> _attachedObjectsById = new();
	private readonly ConditionalWeakTable<object, AttachedObjectData> _attachedObjects = new();
	private byte _attachedMaintain;

	// Links: by-id (RemoveLinkCommand addressing), by structural key (dedup), and an incidence
	// index keyed by node identity (navigation queries). Maintained from LinkAddedEvent/
	// LinkRemovedEvent — links have their own dedicated command/event pair, not the generic
	// object lifecycle. See plans/links.md.
	private readonly ConcurrentDictionary<Guid, Link> _linksById = new();
	private readonly ConcurrentDictionary<LinkKey, Link> _linksByKey = new();
	private readonly ConcurrentDictionary<Guid, List<Link>> _linksByNode = new();

	public bool IsOnline => _eventReplicationService?.IsOnline ?? false;


	public InMemoryProjection(
		  ISbxSerializerFactory serializerFactory
		, ITypeMetadataProvider typeMetadataProvider
		, Guid streamId
		, IAppendStorage<Event, Guid>? eventStorage = null
		, IEventReplicationService? eventReplicationService = null
		, JsonSerializerOptions? jsonSerializerOptions = null
		, JsonSerializerContext? jsonSerializerContext = null
		, IServiceProvider? serviceProvider = null
		)
	{
		// The stream id is a first-class construction value — an InMemoryProjection is inherently
		// single-tenant: one instance materializes exactly one stream's state, so it is pinned to
		// exactly one stream up front and never reads an ambient SynqraStreamContext scope. It is
		// supplied at runtime by IProjectionFactory.Create(streamId) (the caller resolves the stream
		// at the call site — a fresh random stream in tests, the session stream on a client), NOT
		// baked into a DI registration. Unlike the omnitenant Mongo/File stores (which resolve the
		// ambient scope per call when unpinned, via the shared SynqraStreamContext.Resolve), this store
		// returns its pinned stream unconditionally. It does NOT replay itself here — a freshly created
		// projection is cold (Cursor == Guid.Empty); IProjectionKeeper.MaintainAsync folds in the delta
		// from the stream's IEventLog before first use.
		StreamId = streamId;
		if (StreamId == default)
		{
			throw new InvalidOperationException(
				"InMemoryProjection requires an explicit StreamId. Create it via "
				+ "IProjectionFactory.Create(streamId). "
				+ "There is no default stream — a stream id is a security boundary.");
		}
		_serializerFactory = serializerFactory;
		TypeMetadataProvider = typeMetadataProvider;
		_eventStorage = eventStorage;
		_eventReplicationService = eventReplicationService;
		_serviceProvider = serviceProvider;
		_jsonSerializerOptions = jsonSerializerOptions;
		if (jsonSerializerContext != null)
		{
			foreach (var supportedTypeData in jsonSerializerContext.GetType().GetCustomAttributesData().Where(x => x.AttributeType == typeof(JsonSerializableAttribute)))
			{
				var type = (Type)supportedTypeData.ConstructorArguments[0].Value;
				TypeMetadataProvider.RegisterType(type);
			}
		}
		else
		{
			// throw new Exception("Something is wrong! We require JsonSerializerOptions to be registered!");
		}

		if (jsonSerializerOptions != null)
		{
			if (jsonSerializerOptions.Converters.Count == 0)
			{
				throw new Exception("Something is wrong! We require JsonSerializerOptions to have converters registered!");
			}
		}

		// since it is in-memory, we have to roll state in
	}

	public string? ProjectionStatus { get; set; }

	public Guid Cursor { get; private set; }

	/// <summary>
	/// Apply one event, advancing <see cref="Cursor"/> (in <see cref="AfterVisitAsync(Event, EventVisitorContext)"/>).
	/// The <see cref="IProjectionKeeper"/> is the only caller during catch-up; <paramref name="isReplay"/>
	/// suppresses one-shot activator side effects for historical events.
	/// </summary>
	public async Task ApplyAsync(Event ev, bool isReplay = false, CancellationToken cancellationToken = default)
	{
		await ev.AcceptAsync(this, new EventVisitorContext { IsReplay = isReplay });
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
						bm.Attach(this, collection.CollectionId);
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

	Guid GetCollectionId(Type rootType, string? name = null)
	{
		return TypeMetadataProvider.GetTypeMetadata(rootType).GetCollectionId(name);
	}

	public Guid StreamId { get; }

	ISynqraCollection IObjectStore.GetCollection(Type type, string? collectionName)
	{
		return GetCollection(type, collectionName ?? "");
	}

	internal InMemoryStoreCollection GetCollection(Type type, string collectionName)
	{
		LinkApplyHelpers.GuardNotLinkType(type);
		var collectionId = GetCollectionId(type, collectionName ?? throw new ArgumentNullException(nameof(collectionName)));
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_collections, collectionId, out var exists);
		if (!exists || slot == null)
		{
			var gtype = typeof(InMemoryStoreCollection<>).MakeGenericType(type);
			slot = (InMemoryStoreCollection)Activator.CreateInstance(gtype, [this
				, /*streamId*/ StreamId
				, collectionId/*collectionId*/
				, _serializerFactory
#if NET8_0_OR_GREATER
				, _jsonSerializerOptions
#endif
				])!;
		}
		return slot;
#else
		throw new Exception("Not implemented for older frameworks");
#endif
	}

	public ISynqraCollection<T> GetCollection<T>(string? collectionName = null)
		where T : class
	{
		return GetCollectionInternal<T>(collectionName ?? "");
	}

	internal InMemoryStoreCollection<T> GetCollectionInternal<T>(string? collectionName = null) where T : class
	{
		LinkApplyHelpers.GuardNotLinkType(typeof(T));
		var collectionId = GetCollectionId(typeof(T), collectionName ?? "");
#if NET7_0_OR_GREATER
		ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_collections, collectionId, out var exists);
		if (!exists || slot == null)
		{
			var col = new InMemoryStoreCollection<T>(
				  /* store */ this
				, /*streamId*/ StreamId
				, collectionId/*collectionId*/
				, _serializerFactory
#if NET8_0_OR_GREATER
				, _jsonSerializerOptions
#endif
				);
			slot = col;
			return col;
		}
		return (InMemoryStoreCollection<T>)slot;
#else
		throw new Exception("Not implemented for older frameworks");
#endif
	}

	public Task SubmitCommandAsync(ISynqraCommand newCommand, CommandSubmissionOptions? options = null)
	{
		// Normalize first — fill in CommandId, StreamId, and (for SingleObjectCommand)
		// resolve TargetId / CollectionId / TargetTypeId from TargetObject if the caller
		// gave us the model reference but not the ids. This is read-only state lookup,
		// no events emitted, so it is safe to run before the precondition check.
		if (newCommand is Command cmd)
		{
			if (cmd.CommandId == default)
			{
				cmd.CommandId = GuidExtensions.CreateVersion7();
			}
			if (cmd.StreamId == default)
			{
				cmd.StreamId = StreamId;
			}
			else if (cmd.StreamId != StreamId)
			{
				throw new InvalidOperationException(
					$"Command stream {cmd.StreamId} does not match this projection's stream {StreamId} — refusing misrouted command {cmd.CommandId}.");
			}
		}
		if (newCommand is SingleObjectCommand soc)
		{
			if (soc.TargetObject != null)
			{
				var attached = GetAttachedData(soc.TargetObject, default, null, GetMode.RequiredId);
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
				var typeId = TypeMetadataProvider.GetTypeMetadata(soc.TargetObject.GetType()).TypeId; // TODO this might differ from root for hierarchy, do I need root here or a concrete type?
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

		// Optimistic concurrency precondition — runs AFTER normalization so the check
		// sees the resolved TargetId, but BEFORE any event production so a rejected
		// command leaves NO trace in the event stream.
		// Guid.Empty means "I don't care" — same semantic as omitting the precondition.
		if (options is not null
			&& options.ExpectedLastEventId != Guid.Empty
			&& newCommand is SingleObjectCommand precheckSoc
			&& precheckSoc.TargetId != default)
		{
			var actual = GetLastEventId(precheckSoc.TargetId);
			if (actual != options.ExpectedLastEventId)
			{
				throw new ConcurrencyException(precheckSoc.TargetId, options.ExpectedLastEventId, actual);
			}
		}

		return ProcessCommandAsync(newCommand);
	}

	// [UnsupportedOSPlatform("browser")]
	public void SubmitCommand(Command newCommand)
	{
		ProcessCommandAsync(newCommand).GetAwaiter().GetResult();
	}

	/// <summary>
	/// When <c>false</c>, <see cref="CommandCreatedEvent"/>s are still applied in-memory (so
	/// the <c>Command</c> collection is populated) but are NOT written to the durable event
	/// store. Domain events alone rebuild state on replay, so the command audit isn't needed
	/// for correctness.
	/// <para>
	/// This is a temporary opt-out for durable backends (e.g. MongoDB) whose serializer
	/// can't yet take the live command payload (<see cref="SingleObjectCommand.TargetObject"/>
	/// and create-command data are arbitrary CLR objects). Re-enable once command payloads
	/// are persistable — the command log is wanted later for undo/redo.
	/// </para>
	/// </summary>
	public bool PersistCommandEvents { get; set; } = true;

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
		}
		if (_eventStorage != null)
		{
			// Domain events are always durable. CommandCreatedEvents are only persisted when
			// PersistCommandEvents is on — see that property for the temporary opt-out used by
			// backends that can't yet serialize the live command payload.
			var toStore = PersistCommandEvents
				? (IEnumerable<Event>)commandHandlingContext.Events
				: commandHandlingContext.Events.Where(e => e is not CommandCreatedEvent).ToList();
			await _eventStorage.AppendBatchAsync(toStore); // store event in storage and trigger replication
		}
		CommandProcessed?.Invoke(this, EventArgs.Empty);
		_eventReplicationService?.Trigger(cmd, commandHandlingContext.Events);
	}

	public event EventHandler<EventArgs>? CommandProcessed;

	/// <summary>
	/// Process and apply it locally
	/// </summary>
	private async Task ProcessEventAsync(Event newEvent)
	{
		await newEvent.AcceptAsync(this, null);
	}

	#region Command Handler

	public Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx)
	{
		var created = new CommandCreatedEvent
		{
			EventId = GuidExtensions.CreateVersion7(),
			Data = cmd,
			CommandId = cmd.CommandId,
			StreamId = cmd.StreamId,
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
		var type = TypeMetadataProvider.GetTypeMetadata(cmd.TargetTypeId);
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
			StreamId = cmd.StreamId,
			EventId = GuidExtensions.CreateVersion7(),
			CollectionId = cmd.CollectionId,
			CommandId = cmd.CommandId,
			TargetTypeId = cmd.TargetTypeId,
			TargetId = cmd.TargetId,
			// Data = cmd.Data,
			// DataString = cmd.DataJson, // if json is cached here, let's use it to save on serialization
			DataObject = cmd.TargetObject, // or may be entire object
		};
		ctx.Events.Add(created);

		if (false && cmd.TargetObject is IBindableModel bm) // I just found it out while debugging, do not remember why originally, but it is likely because it is not implemented yet (and still not)
		{
			
		}
		else
		{
			var pros = cmd.Data.GetType().GetProperties().Where(x => x.CanWrite && x.CanRead);
			foreach (var pro in pros)
			{
				// Skip computed/navigation accessors flagged as non-persisted (e.g. an edge's typed
				// Source/Target, which merely project the scalar id columns). Persisting them would
				// emit the whole referenced object as a property value and, on replay, set it before
				// the scalar ids — corrupting reconstruction.
				if (pro.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true).Length > 0)
				{
					continue;
				}
				var value = pro.GetValue(cmd.Data);
				// SynqraTypeExtensions
				if (Equals(value, pro.PropertyType.GetDefault()))
				{
					continue;
				}
				if (value != null)
				{
					ctx.Events.Add(new ObjectPropertyChangedEvent
					{
						StreamId = cmd.StreamId,
						CommandId = cmd.CommandId,
						CollectionId = cmd.CollectionId,
						EventId = GuidExtensions.CreateVersion7(),
						TargetTypeId = cmd.TargetTypeId,
						TargetId = cmd.TargetId,
						PropertyName = pro.Name,
						OldValue = null,
						NewValue = value,
					});
				}
			}
		}

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
		var ev = new ObjectPropertyChangedEvent
		{
			StreamId = cmd.StreamId,
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
		ctx.Events.Add(ev);

		return Task.CompletedTask;
	}

	public Task VisitAsync(AddComponentCommand cmd, CommandHandlerContext ctx)
	{
		// Uniqueness / veto checks happen during event apply (where the live
		// ComponentsCollection lives). Command handling here just turns the
		// command into the event — same pattern as ChangeObjectProperty.
		ctx.Events.Add(new ComponentAddedEvent
		{
			StreamId = cmd.StreamId,
			CommandId = cmd.CommandId,
			CollectionId = cmd.CollectionId,
			EventId = GuidExtensions.CreateVersion7(),
			TargetTypeId = cmd.TargetTypeId,
			TargetId = cmd.TargetId,

			ComponentTypeId = cmd.ComponentTypeId,
			ComponentId = cmd.ComponentId,
			Data = cmd.Data,
		});
		return Task.CompletedTask;
	}

	public Task VisitAsync(ChangeComponentPropertyCommand cmd, CommandHandlerContext ctx)
	{
		ctx.Events.Add(new ComponentPropertyChangedEvent
		{
			StreamId = cmd.StreamId,
			CommandId = cmd.CommandId,
			CollectionId = cmd.CollectionId,
			EventId = GuidExtensions.CreateVersion7(),
			TargetTypeId = cmd.TargetTypeId,
			TargetId = cmd.TargetId,

			ComponentTypeId = cmd.ComponentTypeId,
			ComponentId = cmd.ComponentId,
			PropertyName = cmd.PropertyName,
			OldValue = cmd.OldValue,
			NewValue = cmd.NewValue,
		});
		return Task.CompletedTask;
	}

	public Task VisitAsync(DeleteComponentCommand cmd, CommandHandlerContext ctx)
	{
		ctx.Events.Add(new ComponentDeletedEvent
		{
			StreamId = cmd.StreamId,
			CommandId = cmd.CommandId,
			CollectionId = cmd.CollectionId,
			EventId = GuidExtensions.CreateVersion7(),
			TargetTypeId = cmd.TargetTypeId,
			TargetId = cmd.TargetId,

			ComponentTypeId = cmd.ComponentTypeId,
			ComponentId = cmd.ComponentId,
		});
		return Task.CompletedTask;
	}

	public Task VisitAsync(AddLinkCommand cmd, CommandHandlerContext ctx)
	{
		// Structural dedup happens during event apply (where the live link index lives) —
		// same pattern as AddComponentCommand's uniqueness check.
		ctx.Events.Add(new LinkAddedEvent
		{
			StreamId = cmd.StreamId,
			CommandId = cmd.CommandId,
			EventId = GuidExtensions.CreateVersion7(),
			LinkTypeId = cmd.LinkTypeId,
			LinkId = cmd.LinkId,
			SourceId = cmd.SourceId,
			TargetId = cmd.TargetId,
			Data = cmd.Data,
		});
		return Task.CompletedTask;
	}

	public Task VisitAsync(RemoveLinkCommand cmd, CommandHandlerContext ctx)
	{
		ctx.Events.Add(new LinkRemovedEvent
		{
			StreamId = cmd.StreamId,
			CommandId = cmd.CommandId,
			EventId = GuidExtensions.CreateVersion7(),
			LinkId = cmd.LinkId,
		});
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
		// Single apply choke point for every path (local command, keeper catch-up, live). A stream
		// mismatch means a misrouted event — never silently fold it into the wrong projection. A
		// default (unset) StreamId is tolerated as legacy/unrouted in a single-stream log.
		if (ev.StreamId != default && ev.StreamId != StreamId)
		{
			throw new InvalidOperationException(
				$"Event stream {ev.StreamId} does not match projection stream {StreamId} — misrouted event {ev.EventId}.");
		}
		Cursor = ev.EventId;
		return Task.CompletedTask;
	}

	public Task VisitAsync(ObjectCreatedEvent ev, EventVisitorContext ctx)
	{
		var typeMetadata = TypeMetadataProvider.GetTypeMetadata(ev.TargetTypeId);
		var collection = GetCollection(TypeMetadataProvider.GetTypeMetadata(ev.TargetTypeId).Type, "");

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
		/*
		else if (ev.DataString != null)
		{
			newItem = JsonSerializer.Deserialize(ev.DataString, typeMetadata.Type, _jsonSerializerOptions);
		}
		*/
		else if (ev.Data != null)
		{
			newItem = ev.Data; //JsonSerializer.Deserialize(JsonSerializer.Serialize<IDictionary<string, object?>?>(ev.Data, _jsonSerializerOptions), typeMetadata.Type, _jsonSerializerOptions);
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
				ibm.Attach(this, collection.CollectionId);
			}
		}
		collection.AddByEvent(newItem);

		// Record the creation event as the target's "last applied event" so that
		// the first generated-setter write to this fresh object can pre-condition
		// against it via ExpectedLastEventId.
		if (data2 is not null)
		{
			data2.LastEventId = ev.EventId;
		}
		return Task.CompletedTask;
	}

	/// <summary>
	/// Materializes the link from <see cref="AddLinkCommand.Data"/> the same way
	/// <see cref="ComponentApplyHelpers.MaterializeComponent"/> does for a component (live instance / json-shaped dict /
	/// fall back to reflection), attaches it to the store, and indexes it — rejecting a structural
	/// duplicate (same concrete type + endpoint pair) before it can be persisted. Both endpoints are
	/// always already set by the time this runs (the command carries the whole link, unlike a plain
	/// object's properties which trickle in across separate events), so there is no deferred-
	/// indexing step the way the generic object lifecycle would need.
	/// <see cref="LinkAddedEvent.SourceId"/>/<see cref="LinkAddedEvent.TargetId"/> are authoritative —
	/// stamped onto the materialized link explicitly, not read off whatever <see cref="LinkAddedEvent.Data"/>
	/// happened to carry.
	/// </summary>
	Task VisitLinkAddedCore(LinkAddedEvent ev)
	{
		var linkType = TypeMetadataProvider.GetTypeMetadata(ev.LinkTypeId).Type;
		var link = LinkApplyHelpers.MaterializeLink(linkType, ev.Data);
		link.LinkId = ev.LinkId;
		link.SourceId = ev.SourceId;
		link.TargetId = ev.TargetId;

		var key = link.StructuralKey;
		if (!_linksByKey.TryAdd(key, link))
		{
			throw new InvalidOperationException(
				$"LinkAddedEvent {ev.EventId} could not register a '{linkType.Name}' link — an equivalent link already exists between the same endpoints.");
		}
		_linksById[link.LinkId] = link;
		_linksByNode.GetOrAdd(link.SourceId, _ => new List<Link>()).Add(link);
		if (link.TargetId != link.SourceId)
		{
			_linksByNode.GetOrAdd(link.TargetId, _ => new List<Link>()).Add(link);
		}

		if (link is IBindableModel bindable && bindable.Store is null)
		{
			bindable.Attach(this, TypeMetadataProvider.GetTypeMetadata(linkType).GetCollectionId(""));
		}

		NotifyLinkChanged(link, LinkEnd.Source);
		NotifyLinkChanged(link, LinkEnd.Target);
		return Task.CompletedTask;
	}

	Task VisitLinkRemovedCore(LinkRemovedEvent ev)
	{
		if (!_linksById.TryRemove(ev.LinkId, out var link))
		{
			return Task.CompletedTask; // already gone — idempotent
		}
		_linksByKey.TryRemove(link.StructuralKey, out _);
		if (_linksByNode.TryGetValue(link.SourceId, out var fromList))
		{
			fromList.Remove(link);
		}
		if (link.TargetId != link.SourceId && _linksByNode.TryGetValue(link.TargetId, out var toList))
		{
			toList.Remove(link);
		}

		NotifyLinkChanged(link, LinkEnd.Source);
		NotifyLinkChanged(link, LinkEnd.Target);
		return Task.CompletedTask;
	}

	/// <summary>Resolves the endpoint object at <paramref name="selfEnd"/> and, if it implements <see cref="ILinkAware"/>, tells it this link's type changed.</summary>
	void NotifyLinkChanged(Link link, LinkEnd selfEnd)
	{
		var endpointId = selfEnd == LinkEnd.Source ? link.SourceId : link.TargetId;
		if (TryGetModel(endpointId).Model is ILinkAware aware)
		{
			aware.OnLinkChanged(link.GetType(), selfEnd);
		}
	}

	// ---------------------------------------------------------------- ILinkIndex

	IReadOnlyCollection<Link> ILinkIndex.Links => (IReadOnlyCollection<Link>)_linksByKey.Values;

	IReadOnlyList<Link> ILinkIndex.LinksAt(Guid nodeId, LinkEnd end, Type linkType)
	{
		if (!_linksByNode.TryGetValue(nodeId, out var list))
		{
			return Array.Empty<Link>();
		}
		return list.Where(l => linkType.IsInstanceOfType(l) && IncidentAt(l, nodeId, end)).ToArray();
	}

	IReadOnlyList<Link> ILinkIndex.LinksBetween(Guid a, Guid b, Type linkType)
	{
		if (!_linksByNode.TryGetValue(a, out var list))
		{
			return Array.Empty<Link>();
		}
		return list
			.Where(l => linkType.IsInstanceOfType(l)
				&& ((l.SourceId == a && l.TargetId == b) || (l.SourceId == b && l.TargetId == a)))
			.ToArray();
	}

	bool ILinkIndex.TryGetByKey(LinkKey key, out Link? link) => _linksByKey.TryGetValue(key, out link);

	bool ILinkIndex.TryGetById(Guid linkId, out Link? link) => _linksById.TryGetValue(linkId, out link);

	// An undirected link is incident from either side; a directed link matches the requested role.
	static bool IncidentAt(Link link, Guid nodeId, LinkEnd end) => end switch
	{
		LinkEnd.None => throw new ArgumentException("LinkEnd.None is not a valid link end.", nameof(end)),
		LinkEnd.Source => link.SourceId == nodeId,
		LinkEnd.Target => link.TargetId == nodeId,
		_ => link.SourceId == nodeId || link.TargetId == nodeId,
	};

	public Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx)
	{
		var tm = TypeMetadataProvider.GetTypeMetadata(ev.TargetTypeId);
		var col = GetCollection(tm.Type, "");

		TryGetModel(ev.TargetId, out var data);
		if (data.Model is IBindableModel bm)
		{
			bm.Set(ev.PropertyName, ev.NewValue);
		}
		else if (data.Model is not null)
		{
			// throw new Exception($"The type '{data.Model.GetType().Name}' is not IBindableModel. Please add 'partial' keyword for generator to work.");
			var pi = data.Model.GetType().GetProperty(ev.PropertyName) ?? throw new Exception("Property not found");
			var value = ev.NewValue;
			if (ev.NewValue is IConvertible c)
			{
				value = c.ToType(pi.PropertyType, CultureInfo.InvariantCulture);
			}
			pi?.SetValue(data.Model, Convert.ChangeType(value, pi.PropertyType));
		}
		else
		{
			throw new Exception($"Cannot change property of unknown object {ev.TargetId}");
		}

		// Record this event as the target's last-applied event id, for content-addressed
		// optimistic-concurrency checks (the next write will pre-condition against it).
		// We update only when the event is successfully applied — rejected commands
		// leave LastEventId untouched, so a subsequent retry pre-conditioned against
		// the same point in history still matches.
		if (data.Attached is not null)
		{
			data.Attached.LastEventId = ev.EventId;
		}
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public Guid GetLastEventId(Guid targetId)
	{
		if (TryGetModel(targetId, out var data) && data.Attached is not null)
		{
			return data.Attached.LastEventId;
		}
		return Guid.Empty;
	}

	public object? ResolveObject(Guid id) => id == default ? null : TryGetModel(id).Model;

	public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx)
	{
		return Task.CompletedTask;
		// throw new NotImplementedException("ObjectDeletedEvent is not implemented yet");
	}

	public Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx)
	{
		var commands = GetCollection(typeof(Command), "");
		commands.AddByEvent(ev.Data);
		return Task.CompletedTask;
		// throw new NotImplementedException("ObjectDeletedEvent is not implemented yet");
	}

	public Task VisitAsync(ComponentAddedEvent ev, EventVisitorContext ctx)
	{
		if (ev.ComponentId == ev.TargetId)
		{
			// Phase 2 (ECS): a self-owned ROOT COMPONENT is the entity's own data (the collapsed
			// "object", _id == _eid == entityId). Materialize + track it as the entity and add it to
			// its collection — the object-lifecycle path, reached through the component vocabulary.
			var rootType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
			var rootCollection = GetCollection(rootType, "");
			object rootItem = ev.Data ?? Activator.CreateInstance(rootType)!;
			var rootAttached = GetAttachedData(rootItem, ev.TargetId, rootCollection, GetMode.GetOrCreate);
			if (rootItem is IBindableModel ribm && ribm.Store == null)
			{
				ribm.Attach(this, rootCollection.CollectionId);
			}
			rootCollection.AddByEvent(rootItem);
			if (rootAttached is not null)
			{
				rootAttached.LastEventId = ev.EventId;
			}
			return Task.CompletedTask;
		}

		var container = ResolveContainer(ev.TargetId);

		// Instantiate the component. Lookup the concrete type via the type registry,
		// reuse the model-binding pathway so JSON payloads round-trip the same way
		// as for normal SynqraModel objects.
		var componentType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		var component = ComponentApplyHelpers.MaterializeComponent(componentType, ev.Data, ev.ComponentId);

		if (!container.Components.TryAdd(component))
		{
			throw new InvalidOperationException(
				$"ComponentAddedEvent {ev.EventId} could not attach a '{componentType.Name}' to container {ev.TargetId} — uniqueness or veto check rejected it during replay. The event stream is inconsistent.");
		}

		// Wire up the container linkage so the component's generated property
		// setters can build ChangeComponentPropertyCommands without the user
		// having to pass the container reference manually.
		if (component is IBindableComponent bindableComponent)
		{
			bindableComponent.AttachToContainer(
				this,
				ev.TargetId,
				ev.TargetTypeId,
				ev.CollectionId);
		}

		// Activation only fires on the originating event, never on replay.
		// (LoadStateCoreAsync supplies a non-null EventVisitorContext with
		// IsReplay = true; live event processing via ProcessEventAsync passes
		// null, which is treated as "not replay".)
		// Skipped silently when no IServiceProvider was supplied at construction —
		// component activators that need DI would simply fail if called without
		// one, so the projection refuses to start the call.
		var isReplay = ctx is not null && ctx.IsReplay;
		if (component is IActivatableComponent activatable
			&& !isReplay
			&& _serviceProvider is not null)
		{
			activatable.Activate(new ComponentActivationContext
			{
				ServiceProvider = _serviceProvider,
				Container = container,
				ContainerId = ev.TargetId,
				Component = component,
				IsReplay = false,
			});
		}

		// Component attach is a state-changing event on the container — bump
		// the container's LastEventId so optimistic-concurrency precondition
		// against (container, component-edit) catches concurrent edits.
		if (TryGetModel(ev.TargetId, out var data) && data.Attached is not null)
		{
			data.Attached.LastEventId = ev.EventId;
		}
		return Task.CompletedTask;
	}

	public Task VisitAsync(ComponentPropertyChangedEvent ev, EventVisitorContext ctx)
	{
		if (ev.ComponentId == ev.TargetId)
		{
			// Root component property change = the entity's own property change (applies to the
			// tracked entity directly; it has no container).
			if (!TryGetModel(ev.TargetId, out var rd) || rd.Model is null)
			{
				throw new InvalidOperationException($"Cannot apply a root property change to unknown entity {ev.TargetId}.");
			}
			if (rd.Model is IBindableModel rbind)
			{
				rbind.Set(ev.PropertyName, ev.NewValue);
			}
			else
			{
				var rpi = rd.Model.GetType().GetProperty(ev.PropertyName)
					?? throw new InvalidOperationException($"Root entity '{rd.Model.GetType().Name}' has no property '{ev.PropertyName}'.");
				var rv = ev.NewValue;
				if (rv is IConvertible rc) rv = rc.ToType(rpi.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
				rpi.SetValue(rd.Model, rv);
			}
			if (rd.Attached is not null) rd.Attached.LastEventId = ev.EventId;
			return Task.CompletedTask;
		}

		var container = ResolveContainer(ev.TargetId);
		var component = container.ResolveComponent(ev, TypeMetadataProvider);

		// Reuse the bindable-model set path so listeners (INotifyPropertyChanged etc.)
		// fire naturally. Components that don't implement IBindableModel fall back to
		// reflection.
		if (component is IBindableModel bindable)
		{
			bindable.Set(ev.PropertyName, ev.NewValue);
		}
		else
		{
			var pi = component.GetType().GetProperty(ev.PropertyName)
				?? throw new InvalidOperationException(
					$"Component '{component.GetType().Name}' has no property '{ev.PropertyName}'.");
			var value = ev.NewValue;
			if (value is IConvertible c)
			{
				value = c.ToType(pi.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
			}
			pi.SetValue(component, value);
		}

		if (TryGetModel(ev.TargetId, out var data) && data.Attached is not null)
		{
			data.Attached.LastEventId = ev.EventId;
		}
		return Task.CompletedTask;
	}

	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx)
	{
		if (ev.ComponentId == ev.TargetId)
		{
			// Root entity delete — full delete/cascade is Phase 4; keep parity with the current
			// ObjectDeleted no-op so replaying such an event doesn't fault.
			return Task.CompletedTask;
		}

		var container = ResolveContainer(ev.TargetId);
		var component = container.ResolveComponent(ev, TypeMetadataProvider);

		// BypassRemove rather than Remove: when the container is wrapped in
		// StoreBoundComponentsCollection, the ICollection<T>.Remove path emits a
		// command. The projection is APPLYING an event, so it must skip the command
		// channel — otherwise it would generate a recursive delete command.
		if (!container.Components.BypassRemove(component))
		{
			throw new InvalidOperationException(
				$"ComponentDeletedEvent {ev.EventId}: component instance was located but the collection refused to remove it. The event stream is inconsistent.");
		}

		if (TryGetModel(ev.TargetId, out var data) && data.Attached is not null)
		{
			data.Attached.LastEventId = ev.EventId;
		}
		return Task.CompletedTask;
	}

	public Task VisitAsync(LinkAddedEvent ev, EventVisitorContext ctx) => VisitLinkAddedCore(ev);

	public Task VisitAsync(LinkRemovedEvent ev, EventVisitorContext ctx) => VisitLinkRemovedCore(ev);

	// ---- Component apply helpers ----

	IComponentContainer ResolveContainer(Guid targetId)
	{
		TryGetModel(targetId, out var data);
		return ComponentApplyHelpers.ResolveContainer(data.Model, targetId);
	}

	#endregion


}

internal class AttachedObjectData
{
	public required Guid Id { get; init; }
	public required StoreCollection Collection { get; init; }
	public required bool IsJustCreated { get; set; }

	/// <summary>
	/// Id of the last event applied to this target. Updated by event-visitor handlers
	/// each time an event touches this object. Used for optimistic concurrency checks via
	/// <see cref="CommandSubmissionOptions.ExpectedLastEventId"/> — same idea as
	/// <c>git push --force-with-lease=&lt;sha&gt;</c>.
	/// </summary>
	public Guid LastEventId { get; set; }
}

// It is not flags, as all possible permutations are defined explicitly
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
	internal static Guid GetId(this IObjectStore ctx, object model, StoreCollection? collection, GetMode mode)
	{
		return ((InMemoryProjection)ctx).GetId(model, collection, mode);
	}

	internal static AttachedObjectData Attach(this IObjectStore ctx, object model, StoreCollection collection)
	{
		return ((InMemoryProjection)ctx).Attach(model, collection);
	}

	internal static (bool IsJustCreated, Guid Id) GetOrCreateId(this IObjectStore ctx, object model, StoreCollection collection)
	{
		return ((InMemoryProjection)ctx).GetOrCreateId(model, collection);
	}
}
