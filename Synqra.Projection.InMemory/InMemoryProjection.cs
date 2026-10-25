using Microsoft.Extensions.Logging;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using Synqra.Projection;
using Synqra.Projection.InMemory;
using System;
using System.Collections.Concurrent;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

using IAppendStorage = IEventLog;

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
public class InMemoryProjection : IObjectStore, IProjection, ICommandVisitor<CommandHandlerContext>, IEventVisitor<EventVisitorContext>, ILinkIndex, IComponentIndex, IReplayProjection, ICommittedEventSource
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
	private readonly ISynqraIdProvider _ids;
	public ISynqraIdProvider IdProvider => _ids;
	private readonly Dictionary<Guid, InMemoryStoreCollection> _collections = new();
	private readonly ConcurrentDictionary<Guid, StrongReference> _attachedObjectsById = new();
	private readonly ConditionalWeakTable<object, AttachedObjectData> _attachedObjects = new();
	private byte _attachedMaintain;

	// Links are components of their source entity (docs/model.md §4); on top of the owner's Components the
	// store keeps them by id, by structural key (dedup) and by either endpoint (navigation and the reverse
	// side, which is a view here, never a second stored record).
	private readonly ConcurrentDictionary<Guid, Link> _linksById = new();
	// facet id -> the entity it hangs off: IComponentIndex.FindComponent without walking every object
	private readonly ConcurrentDictionary<Guid, Guid> _componentOwners = new();
	private readonly ConcurrentDictionary<LinkKey, Link> _linksByKey = new();
	// The outer ConcurrentDictionary only ever made the *lookup* safe — the values were plain lists that
	// link adds/removes mutated while LinksAt/LinksBetween read them. Append-ordered concurrent
	// lists instead, so navigation enumerates safely without copying and link order is still the order
	// links were added (nav collections are user-visible, e.g. a node's children).
	private readonly ConcurrentDictionary<Guid, ConcurrentAppendList<Link>> _linksByNode = new();

	public bool IsOnline => _eventReplicationService?.IsOnline ?? false;


	public InMemoryProjection(
		  ISbxSerializerFactory serializerFactory
		, ITypeMetadataProvider typeMetadataProvider
		, Guid streamId
		, IEventLog? eventStorage = null
		, IEventReplicationService? eventReplicationService = null
		, JsonSerializerOptions? jsonSerializerOptions = null
		, JsonSerializerContext? jsonSerializerContext = null
		, IServiceProvider? serviceProvider = null
		, ISynqraIdProvider? idProvider = null
		)
	{
		_ids = idProvider ?? SynqraIdProvider.Default;
		// The stream id is a first-class construction value — an InMemoryProjection is inherently
		// single-tenant: one instance materializes exactly one stream's state, so it is pinned to
		// exactly one stream up front and never reads an ambient SynqraStreamContext scope. It is
		// supplied at runtime by IProjectionFactory.Create(streamId) (the caller resolves the stream
		// at the call site — a fresh random stream in tests, the session stream on a client), NOT
		// baked into a DI registration. Unlike the omnitenant Mongo/File stores (which resolve the
		// ambient scope per call when unpinned, via the shared SynqraStreamContext.Resolve), this store
		// returns its pinned stream unconditionally. It does NOT replay itself here — a freshly created
		// projection is cold (Cursor == Guid.Empty); IProjectionKeeper.MaintainAsync folds in the delta
		// from the event log before first use.
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

	/// <summary>This projection's own events: applied when submitted, still ahead of <see cref="Cursor"/> in the log.</summary>
	readonly HashSet<Guid> _ownAhead = [];

	/// <summary>
	/// Apply one event, advancing <see cref="Cursor"/> (in <see cref="AfterVisitAsync(Event, EventVisitorContext)"/>).
	/// The <see cref="IProjectionKeeper"/> is the only caller during catch-up; <paramref name="isReplay"/>
	/// suppresses one-shot activator side effects for historical events.
	/// </summary>
	public Task ApplyAsync(Event ev, bool isReplay = false, CancellationToken cancellationToken = default)
		=> _gate.RunAsync(() => ev.AcceptAsync(this, new EventVisitorContext { IsReplay = isReplay })); // replicated and replayed events serialize with submits

	/// <summary>
	/// The whole catch-up is one write: the cursor is read and the log enumerated under the gate, so a
	/// command landing meanwhile waits instead of being applied once by itself and once more from the log.
	/// The cursor is a position in the log (arrival order), so everything after it is new, whoever wrote it,
	/// except this projection's own events, applied when submitted: the cursor only steps over those.
	/// </summary>
	public Task CatchUpAsync(Func<Guid, IAsyncEnumerable<Event>> readAfter, bool isReplay = false, CancellationToken cancellationToken = default)
		=> _gate.RunAsync(async () =>
		{
			var staging = EmergencyLog.Default.IsStaging();
			var applied = 0;
			if (staging)
			{
				EmergencyLog.Default.Stage($"projection {StreamId}: catch-up from {Cursor} (replay {isReplay})");
			}
			await foreach (var ev in readAfter(Cursor).WithCancellation(cancellationToken))
			{
				if (staging && ++applied % 512 == 0)
				{
					EmergencyLog.Default.Stage($"projection {StreamId}: catch-up at {applied} events");
				}
				if (_ownAhead.Remove(ev.EventId))
				{
					Advance(ev.SequenceId);
				}
				else
				{
					try
					{
						await ev.AcceptAsync(this, new EventVisitorContext { IsReplay = isReplay });
					}
					catch (InvalidOperationException refused)
					{
						// The log holds what this projection cannot take (a local facet in a slot the master filled with its own
						// event): stepped over, logged, never a wall. Stopping here left the cursor before it, so every catch-up
						// and every page load hit it again and nothing after it was ever applied (prod 2026-10-22).
						EmergencyLog.Default.LogError(refused, $"InMemoryProjection: event {ev.EventId} of stream {StreamId} refused during catch-up and stepped over; this projection differs from the log there.");
						Advance(ev.SequenceId);
					}
				}
			}
			if (staging)
			{
				EmergencyLog.Default.Stage($"projection {StreamId}: catch-up done, {applied} events, cursor {Cursor}");
			}
		});


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
						id = _ids.CreateComponentId();
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
				Id = _ids.CreateComponentId(),
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
						Id = _ids.CreateComponentId(),
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
#pragma warning disable CS0618 // named-collection plumbing until the CollectionId wire-format PR
		return TypeMetadataProvider.GetTypeMetadata(rootType).GetCollectionId(name);
#pragma warning restore CS0618
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

	// One write at a time (a submit, a replicated or replayed event): the precondition check and the
	// apply of the produced events happen under this gate, so nothing lands between a check and its write.
	readonly ReentrantGate _gate = new();

	public async Task SubmitCommandAsync(ISynqraCommand newCommand, CommandSubmissionOptions? options = null)
	{
		// Normalize first — fill in CommandId and StreamId, and resolve the target ids from
		// TargetObject if the caller gave us the model reference but not the ids. This is
		// read-only state lookup, no events emitted, so it is safe to run before the precondition check.
		if (newCommand is Command cmd)
		{
			if (cmd.CommandId == default)
			{
				cmd.CommandId = _ids.CreateCommandId(SynqraIdProviderExtensions.DeclaredTypeId(cmd.GetType()));
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
		if (newCommand is Command normalized)
		{
			ResolveTargetIds(normalized);
		}

		await _gate.RunAsync(async () =>
		{
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
			await ProcessCommandAsync(newCommand);
		});
	}

	/// <summary>Fills a command's target ids from its live TargetObject when the caller gave the instance, not the ids.</summary>
	void ResolveTargetIds(Command cmd)
	{
		if (cmd is SingleObjectCommand soc && soc.TargetObject != null)
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

	// [UnsupportedOSPlatform("browser")]
	public void SubmitCommand(Command newCommand)
	{
		_gate.Run(() =>
		{
			using var _ = new NoSynchronizationContext();
			ProcessCommandAsync(newCommand).GetAwaiter().GetResult();
		});
	}

	/// <summary>
	/// Whether commands emit their <see cref="CommandCreatedEvent"/> (the command log, wanted for undo/redo); see
	/// <see cref="CommandHandlerContext.EmitCommandEvents"/>.
	/// </summary>
	public bool EmitCommandEvents { get; set; } = true;

	/// <summary>
	/// Process and apply it locally
	/// </summary>
	private async Task ProcessCommandAsync(ISynqraCommand newCommand)
	{
		var commandHandlingContext = new CommandHandlerContext { EmitCommandEvents = EmitCommandEvents };
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
			await _eventStorage.AppendBatchAsync(commandHandlingContext.Events); // store event in storage and trigger replication
			// Another writer (a second browser tab, this host's replication) may have appended before these: jumping the
			// cursor to them would skip that event for good. Catch up instead, stepping over our own (applied above).
			foreach (var @event in commandHandlingContext.Events)
			{
				_ownAhead.Add(@event.EventId);
			}
			var log = _eventStorage;
			await CatchUpAsync(after => log.ReadStreamAsync(StreamId, after));
		}
		CommandProcessed?.Invoke(this, EventArgs.Empty);
		_eventReplicationService?.Trigger(cmd, commandHandlingContext.Events);
		if (commandHandlingContext.Events.Count > 0)
		{
			EventsCommitted?.Invoke(commandHandlingContext.Events);
		}
	}

	public event Action<IEnumerable<Event>>? EventsCommitted;

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
		// One command event per submitted command; a batch is one (its children run with command events off).
		if (ctx.EmitCommandEvents)
		{
			ctx.Events.Add(new CommandCreatedEvent
			{
				EventId = _ids.CreateEventId<CommandCreatedEvent>(cmd.CommandId, 0),
				Data = cmd,
				CommandId = cmd.CommandId,
				StreamId = cmd.StreamId,
			});
		}
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

			EventId = _ids.CreateEventId<ObjectPropertyChangedEvent>(cmd.CommandId, 1),
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
		// A facet component reaches us with no id (POCOs no longer self-mint); assign one from the
		// injected provider and stamp it back onto the live instance so the consumer's Id is stable.
		if (cmd.ComponentId == default)
		{
			cmd.ComponentId = _ids.CreateComponentId();
			if (cmd.Data is IBindableComponent bindable)
			{
				bindable.SetComponentId(cmd.ComponentId);
			}
		}
		ctx.Events.Add(new ComponentAddedEvent
		{
			StreamId = cmd.StreamId,
			CommandId = cmd.CommandId,
			CollectionId = cmd.CollectionId,
			EventId = _ids.CreateEventId<ComponentAddedEvent>(cmd.CommandId, 1),
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
			EventId = _ids.CreateEventId<ComponentPropertyChangedEvent>(cmd.CommandId, 1),
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
			EventId = _ids.CreateEventId<ComponentDeletedEvent>(cmd.CommandId, 1),
			TargetTypeId = cmd.TargetTypeId,
			TargetId = cmd.TargetId,

			ComponentTypeId = cmd.ComponentTypeId,
			ComponentId = cmd.ComponentId,
		});
		return Task.CompletedTask;
	}

	/// <summary>
	/// A batch is its children's events applied in one submit: the expectations are checked first,
	/// then each child produces its events in order and they are applied and stored together. The
	/// in-memory state is a replica rebuilt from the log; an apply refused halfway leaves nothing in
	/// the log, and the state is rebuilt from it.
	/// </summary>
	public async Task VisitAsync(BatchCommand cmd, CommandHandlerContext ctx)
	{
		// The batch is the unit of intent (and of undo): its own command event carries the children, they emit none.
		var inner = new CommandHandlerContext { Events = ctx.Events, EmitCommandEvents = false };
		foreach (var expectation in cmd.Expectations)
		{
			if (expectation.ExpectedLastEventId == Guid.Empty)
			{
				continue;
			}
			var actual = GetLastEventId(expectation.TargetId);
			if (actual != expectation.ExpectedLastEventId)
			{
				throw new ConcurrencyException(expectation.TargetId, expectation.ExpectedLastEventId, actual);
			}
		}
		foreach (var child in cmd.Commands)
		{
			if (child is BatchCommand)
			{
				throw new NotSupportedException("A batch cannot contain another batch.");
			}
			if (child.CommandId == default)
			{
				child.CommandId = _ids.CreateCommandId(SynqraIdProviderExtensions.DeclaredTypeId(child.GetType()));
			}
			if (child.StreamId == default)
			{
				child.StreamId = cmd.StreamId;
			}
			else if (child.StreamId != cmd.StreamId)
			{
				throw new InvalidOperationException($"Command {child.CommandId} of stream {child.StreamId} cannot run in a batch of stream {cmd.StreamId}.");
			}
			ResolveTargetIds(child);
			await child.AcceptAsync(this, inner);
		}
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
		Advance(ev.SequenceId);
		return Task.CompletedTask;
	}

	/// <summary>Moves <see cref="Cursor"/> forward to a log position; an event not yet stamped (own, before its append) leaves it.</summary>
	void Advance(Guid sequence)
	{
		if (sequence != default && sequence.CompareBigEndian(Cursor) > 0)
		{
			Cursor = sequence;
		}
	}

	// ---------------------------------------------------------------- IComponentIndex

	public ComponentHit? FindComponent(Guid componentId)
	{
		ComponentHit? hit = null;
		if (_componentOwners.TryGetValue(componentId, out var owner)
			&& TryGetModel(owner, out var data) && data.Model is IComponentContainer container
			&& container.Components.FirstOrDefault(c => c is IIdentifiable<Guid> identified && identified.Id == componentId) is { } component)
		{
			hit = new ComponentHit(container, component);
		}
		return hit;
	}

	/// <summary>The typed collections of every registered concrete type the query names, filtered and put in id order (in memory the collections are the index).</summary>
	public IEnumerable<object> FindObjects(ObjectQuery query)
	{
		var types = TypeMetadataProvider.RegisteredTypes
			.Select(m => m.Type)
			.Where(t => !t.IsAbstract && query.Types.Any(q => q.IsAssignableFrom(t)))
			.Distinct();
		var words = query.Words;
		return types
			.SelectMany(t => GetCollection(t, "").Cast<object>().Where(t.IsInstanceOfType))
			.Select(o => (Id: GetId(o), Model: o))
			.Where(x => x.Id.CompareTo(query.After) > 0)
			.Where(x => query.Ids is null || query.Ids.Contains(x.Id))
			.Where(x => query.Has is null || (x.Model is IComponentContainer c && c.Components.Any(query.Has.IsInstanceOfType)))
			.Where(x => words.All(w => Mentions(x.Model, w)))
			.OrderBy(x => x.Id)
			.Select(x => x.Model);
	}

	// The Mongo text index's contract without one: the word whole, case-insensitive, in a string or type name of the object or a facet.
	bool Mentions(object model, string word)
		=> (model is IComponentContainer container ? container.Components.Cast<object>().Prepend(model) : [model])
			.SelectMany(EnumerateTexts)
			.Any(text => HoldsWord(text, word));

	// The type name and the public string (and string collection) properties: what Mongo's $** text index sees of a document.
	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "A property trimmed away is not searchable; nothing else depends on it.")]
	IEnumerable<string> EnumerateTexts(object model)
		=> model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(string) || typeof(IEnumerable<string>).IsAssignableFrom(p.PropertyType)))
			.SelectMany(p => p.GetValue(model) switch { string s => [s], IEnumerable<string> many => many, _ => [] })
			.Prepend(model.GetType().Name);

	// A case-insensitive occurrence not glued to a letter or digit on either side.
	bool HoldsWord(string text, string word)
	{
		var holds = false;
		var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
		while (!holds && at >= 0)
		{
			var end = at + word.Length;
			holds = (at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]));
			at = holds ? at : text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase);
		}
		return holds;
	}

	// ---------------------------------------------------------------- ILinkIndex

	IReadOnlyCollection<Link> ILinkIndex.Links => (IReadOnlyCollection<Link>)_linksByKey.Values;

	IReadOnlyList<Link> ILinkIndex.LinksAt(Guid nodeId, LinkEnd end, Type linkType)
	{
		if (!_linksByNode.TryGetValue(nodeId, out var links))
		{
			return Array.Empty<Link>();
		}
		var result = new List<Link>();
		foreach (var l in links)
		{
			if (linkType.IsInstanceOfType(l) && IncidentAt(l, nodeId, end))
			{
				result.Add(l);
			}
		}
		return result;
	}

	IReadOnlyList<Link> ILinkIndex.LinksBetween(Guid a, Guid b, Type linkType)
	{
		if (!_linksByNode.TryGetValue(a, out var links))
		{
			return Array.Empty<Link>();
		}
		var result = new List<Link>();
		foreach (var l in links)
		{
			if (linkType.IsInstanceOfType(l)
				&& ((l.SourceId == a && l.TargetId == b) || (l.SourceId == b && l.TargetId == a)))
			{
				result.Add(l);
			}
		}
		return result;
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
			if (TryGetModel(ev.TargetId, out var existing) && !ReferenceEquals(existing.Model, ev.Data))
			{
				// Added again under another event (a device seeded this id before its replay caught up): the entity stays as it is, as MongoProjection keeps it.
				existing.Attached!.LastEventId = ev.EventId;
			}
			else
			{
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
			}
			return Task.CompletedTask;
		}

		if (TypeMetadataProvider.IsLinkComponent(ev.ComponentTypeId))
		{
			ApplyLinkAdded(ev);
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

		_componentOwners[ev.ComponentId] = ev.TargetId;

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
		TryGetModel(ev.TargetId, out var data);
		if (ev.ComponentId != ev.TargetId && _linksById.TryGetValue(ev.ComponentId, out var link))
		{
			ApplyLinkChanged(ev, link);
		}
		else
		{
			var target = ComponentApplyHelpers.ResolveTarget(data.Model, ev, TypeMetadataProvider);
			ComponentApplyHelpers.ApplyPropertyChange(target, ev.PropertyName, ev.NewValue);
		}

		if (data.Attached is not null)
		{
			data.Attached.LastEventId = ev.EventId;
		}
		return Task.CompletedTask;
	}

	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx)
	{
		if (ev.ComponentId == ev.TargetId)
		{
			// Root entity delete: drop it from its collection + tracking, with its facets and every
			// link incident to it (owned ones go with it; a link pointing at it is removed from its owner).
			if (TryGetModel(ev.TargetId, out var rd) && rd.Model is not null)
			{
				RemoveIncidentLinks(ev.TargetId);
				var rootType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
				GetCollection(rootType, "").RemoveByEvent(rd.Model);
				if (rd.Model is IComponentContainer deleted)
				{
					foreach (var facet in deleted.Components.OfType<IIdentifiable<Guid>>())
					{
						_componentOwners.TryRemove(facet.Id, out _);
					}
				}
				Untrack(ev.TargetId, rd.Model);
			}
			return Task.CompletedTask;
		}

		if (_linksById.ContainsKey(ev.ComponentId))
		{
			RemoveLink(ev.ComponentId);
			if (TryGetModel(ev.TargetId, out var linkOwner) && linkOwner.Attached is not null)
			{
				linkOwner.Attached.LastEventId = ev.EventId;
			}
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
		_componentOwners.TryRemove(ev.ComponentId, out _);

		if (TryGetModel(ev.TargetId, out var data) && data.Attached is not null)
		{
			data.Attached.LastEventId = ev.EventId;
		}
		return Task.CompletedTask;
	}

	// ---- Link apply helpers ----

	/// <summary>
	/// A link component lands on its owner (the source): the owner is stamped as <see cref="Link.SourceId"/>,
	/// a structural duplicate is refused, a container's Components enforce arity, and both endpoints are told.
	/// </summary>
	void ApplyLinkAdded(ComponentAddedEvent ev)
	{
		var linkType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		if (ev.ComponentId == default)
		{
			throw new InvalidOperationException($"ComponentAddedEvent {ev.EventId} carries no id for its '{linkType.Name}' link; ids are minted at command time, so every replica must agree on it.");
		}
		var link = (Link)ComponentApplyHelpers.MaterializeComponent(linkType, ev.Data, ev.ComponentId);
		link.SourceId = ev.TargetId;
		if (!TryGetModel(ev.TargetId, out var owner) || owner.Model is null)
		{
			throw new InvalidOperationException($"ComponentAddedEvent {ev.EventId}: no entity {ev.TargetId} to own a '{linkType.Name}' link.");
		}
		var key = link.StructuralKey;
		if (!_linksByKey.TryAdd(key, link))
		{
			throw new InvalidOperationException(
				$"ComponentAddedEvent {ev.EventId} could not register a '{linkType.Name}' link — an equivalent link already exists between the same endpoints.");
		}
		if (owner.Model is IComponentContainer container && !container.Components.TryAdd(link))
		{
			_linksByKey.TryRemove(key, out _);
			throw new InvalidOperationException(
				$"ComponentAddedEvent {ev.EventId} could not attach a '{linkType.Name}' link to {ev.TargetId} — uniqueness or veto check rejected it.");
		}
		_componentOwners[ev.ComponentId] = ev.TargetId;
		_linksById[ev.ComponentId] = link;
		IndexLinkEnd(link.SourceId, link);
		if (link.TargetId != link.SourceId)
		{
			IndexLinkEnd(link.TargetId, link);
		}
		((IBindableComponent)link).AttachToContainer(this, ev.TargetId, ev.TargetTypeId, ev.CollectionId);
		if (owner.Attached is not null)
		{
			owner.Attached.LastEventId = ev.EventId;
		}
		NotifyLinkChanged(link.SourceId, link, LinkEnd.Source);
		NotifyLinkChanged(link.TargetId, link, LinkEnd.Target);
	}

	/// <summary>A link edit is in place; retargeting moves it between endpoint indexes and re-checks its structural key.</summary>
	void ApplyLinkChanged(ComponentPropertyChangedEvent ev, Link link)
	{
		LinkApplyHelpers.GuardSourceUnchanged(ev);
		if (ev.PropertyName == nameof(Link.TargetId))
		{
			RetargetLink(ev, link);
		}
		else
		{
			ComponentApplyHelpers.ApplyPropertyChange(link, ev.PropertyName, ev.NewValue);
		}
	}

	void RetargetLink(ComponentPropertyChangedEvent ev, Link link)
	{
		var oldTarget = link.TargetId;
		var oldKey = link.StructuralKey;
		ComponentApplyHelpers.ApplyPropertyChange(link, ev.PropertyName, ev.NewValue);
		var newKey = link.StructuralKey;
		if (!newKey.Equals(oldKey) && !_linksByKey.TryAdd(newKey, link))
		{
			ComponentApplyHelpers.ApplyPropertyChange(link, ev.PropertyName, oldTarget);
			throw new InvalidOperationException(
				$"ComponentPropertyChangedEvent {ev.EventId} could not retarget '{link.GetType().Name}' link {link.LinkId} — an equivalent link already exists between the new endpoints.");
		}
		if (!newKey.Equals(oldKey))
		{
			_linksByKey.TryRemove(oldKey, out _);
		}
		if (oldTarget != link.SourceId)
		{
			UnindexLinkEnd(oldTarget, link);
		}
		if (link.TargetId != link.SourceId)
		{
			IndexLinkEnd(link.TargetId, link);
		}
		NotifyLinkChanged(oldTarget, link, LinkEnd.Target);
		NotifyLinkChanged(link.TargetId, link, LinkEnd.Target);
		NotifyLinkChanged(link.SourceId, link, LinkEnd.Source);
	}

	/// <summary>Drops a link from every index and from its owner's Components; neither endpoint entity is touched.</summary>
	void RemoveLink(Guid linkId)
	{
		if (_linksById.TryRemove(linkId, out var link))
		{
			_linksByKey.TryRemove(link.StructuralKey, out _);
			_componentOwners.TryRemove(linkId, out _);
			UnindexLinkEnd(link.SourceId, link);
			if (link.TargetId != link.SourceId)
			{
				UnindexLinkEnd(link.TargetId, link);
			}
			if (TryGetModel(link.SourceId).Model is IComponentContainer owner)
			{
				owner.Components.BypassRemove(link);
			}
			NotifyLinkChanged(link.SourceId, link, LinkEnd.Source);
			NotifyLinkChanged(link.TargetId, link, LinkEnd.Target);
		}
	}

	/// <summary>The cascade of an entity delete: every link it owns or that points at it.</summary>
	void RemoveIncidentLinks(Guid entityId)
	{
		if (_linksByNode.TryGetValue(entityId, out var incident))
		{
			var snapshot = new List<Guid>();
			foreach (var link in incident)
			{
				snapshot.Add(link.LinkId);
			}
			foreach (var linkId in snapshot)
			{
				RemoveLink(linkId);
			}
		}
	}

	void IndexLinkEnd(Guid endpointId, Link link)
		=> _linksByNode.GetOrAdd(endpointId, _ => new ConcurrentAppendList<Link>()).Add(link);

	void UnindexLinkEnd(Guid endpointId, Link link)
	{
		if (_linksByNode.TryGetValue(endpointId, out var links))
		{
			links.Remove(link);
		}
	}

	/// <summary>Tells the endpoint object, if it implements <see cref="ILinkAware"/>, that a link of this type changed at its end.</summary>
	void NotifyLinkChanged(Guid endpointId, Link link, LinkEnd selfEnd)
	{
		if (TryGetModel(endpointId).Model is ILinkAware aware)
		{
			aware.OnLinkChanged(link.GetType(), selfEnd);
		}
	}

	// ---- Component apply helpers ----

	IComponentContainer ResolveContainer(Guid targetId)
	{
		TryGetModel(targetId, out var data);
		return ComponentApplyHelpers.ResolveContainer(data.Model, targetId);
	}

	void Untrack(Guid id, object model)
	{
		_attachedObjects.Remove(model);
		_attachedObjectsById.TryRemove(id, out _);
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
