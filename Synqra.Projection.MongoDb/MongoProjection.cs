using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;

namespace Synqra.Projection.MongoDb;

using IAppendStorage = IAppendStorage<Event, Guid>;

/// <summary>
/// A MongoDB-backed Synqra projection: it processes commands into events, appends those events to the
/// durable event log (<see cref="IAppendStorage{Event, Guid}"/>), and materializes the resulting object
/// state as documents in MongoDB — one physical collection per type, one document per object keyed by the
/// object's id (<c>_id</c>) and partitioned by its Synqra collection id (<c>_cid</c>).
/// <para>
/// Unlike the in-memory projection (which rebuilds RAM state by replaying the whole log on startup), the
/// materialized read-model is itself durable, so a restarted projection serves current state straight
/// from Mongo with no replay. Documents are stored as the model's JSON shape (round-tripped through the
/// registered <see cref="JsonSerializerOptions"/>). Components are stored on their container document
/// under a projection-controlled <c>_c</c> array (with each component's type id) and rehydrated on read,
/// because a container's components are not part of its own JSON round-trip. Wires are materialized into
/// a dedicated collection.
/// </para>
/// </summary>
public sealed class MongoProjection : IObjectStore, IProjection
{
	const string WiresCollectionName = "_synqra_wires";

	static MongoProjection()
	{
		AppContext.SetSwitch("Synqra.GuidExtensions.ValidateNamespaceIdHashChain", false);
	}

	readonly IMongoDatabase _database;
	readonly ISbxSerializerFactory _serializerFactory;
	readonly IAppendStorage? _eventStorage;
	readonly JsonSerializerOptions _jsonSerializerOptions;
	readonly ISynqraComponentActivator? _componentActivator;

	// Tracking: model instance <-> id, so generated setters can route ChangeObjectPropertyCommands and
	// GetId() resolves a live instance to its key. A strong id->model map keeps tracked instances alive
	// for the projection's lifetime (acceptable for the v1 store; a weak/eviction policy comes later).
	readonly ConditionalWeakTable<object, TrackedObject> _byModel = new();
	readonly ConcurrentDictionary<Guid, object> _byId = new();
	readonly Dictionary<Guid, MongoStoreCollection> _collections = new();
	readonly object _collectionsGate = new();

	// Wires: durable in the WiresCollectionName collection, plus in-memory routing indexes (parity with
	// the in-memory projection) so GetWiresFrom / GetWiresTo answer without a round-trip.
	readonly ConcurrentDictionary<Guid, Wire> _wiresById = new();
	readonly ConcurrentDictionary<PortRef, List<Wire>> _wiresFrom = new();
	readonly ConcurrentDictionary<PortRef, List<Wire>> _wiresTo = new();
	readonly object _wireIndexLock = new();

	public ITypeMetadataProvider TypeMetadataProvider { get; }

	public Guid StreamId => SynqraGuids.SynqraRootStreamId;

	public MongoProjection(
		  IMongoDatabase database
		, ISbxSerializerFactory serializerFactory
		, ITypeMetadataProvider typeMetadataProvider
		, IAppendStorage<Event, Guid>? eventStorage = null
		, JsonSerializerOptions? jsonSerializerOptions = null
		, JsonSerializerContext? jsonSerializerContext = null
		, ISynqraComponentActivator? componentActivator = null
		)
	{
		_database = database ?? throw new ArgumentNullException(nameof(database));
		_serializerFactory = serializerFactory;
		TypeMetadataProvider = typeMetadataProvider;
		_eventStorage = eventStorage;
		_componentActivator = componentActivator;
		_jsonSerializerOptions = jsonSerializerOptions ?? throw new ArgumentException("MongoProjection requires JsonSerializerOptions to materialize documents.", nameof(jsonSerializerOptions));

		if (jsonSerializerContext is not null)
		{
			foreach (var data in jsonSerializerContext.GetType().GetCustomAttributesData().Where(x => x.AttributeType == typeof(JsonSerializableAttribute)))
			{
				TypeMetadataProvider.RegisterType((Type)data.ConstructorArguments[0].Value!);
			}
		}
	}

	sealed class TrackedObject
	{
		public required Guid Id { get; init; }
		public required Guid CollectionId { get; init; }
		public Guid LastEventId { get; set; }
	}

	// ---------------------------------------------------------------- IObjectStore

	ISynqraCollection IObjectStore.GetCollection(Type type, string? collectionName) => GetCollection(type, collectionName ?? "");

	public ISynqraCollection<T> GetCollection<T>(string? collectionName = null) where T : class
		=> (ISynqraCollection<T>)GetCollection(typeof(T), collectionName ?? "");

	internal MongoStoreCollection GetCollection(Type type, string collectionName)
	{
		var collectionId = TypeMetadataProvider.GetTypeMetadata(type).GetCollectionId(collectionName ?? "");
		lock (_collectionsGate)
		{
			if (_collections.TryGetValue(collectionId, out var existing))
			{
				return existing;
			}
			var mongo = _database.GetCollection<BsonDocument>(MongoCollectionName(type));
			var gtype = typeof(MongoStoreCollection<>).MakeGenericType(type);
			var created = (MongoStoreCollection)Activator.CreateInstance(gtype, this, StreamId, collectionId, _serializerFactory, mongo)!;
			_collections[collectionId] = created;
			return created;
		}
	}

	// One physical Mongo collection per type (human-readable name). Multiple Synqra collections of the
	// same type (named collections) share it and are partitioned by the "_cid" field on each document,
	// so the physical name is derivable identically from a type alone — whether we hold the collection
	// name (GetCollection) or only the collection id from an event (Upsert).
	static string MongoCollectionName(Type type) => type.Name;

	public Guid GetId(object model)
	{
		if (_byModel.TryGetValue(model, out var tracked))
		{
			return tracked.Id;
		}
		throw new InvalidOperationException("The object is not attached to this MongoProjection.");
	}

	public Guid GetLastEventId(Guid targetId)
		=> _byId.TryGetValue(targetId, out var model) && _byModel.TryGetValue(model, out var tracked)
			? tracked.LastEventId
			: Guid.Empty;

	public Task SubmitCommandAsync(ISynqraCommand newCommand, CommandSubmissionOptions? options = null)
	{
		if (newCommand is not Command cmd)
		{
			throw new ArgumentException("Only Synqra.Command implementations are supported.", nameof(newCommand));
		}
		if (cmd.CommandId == default)
		{
			cmd.CommandId = GuidExtensions.CreateVersion7();
		}
		if (cmd.StreamId == default)
		{
			cmd.StreamId = StreamId;
		}
		if (cmd is SingleObjectCommand soc)
		{
			// Resolve the target either from the live object reference or, for manually-built commands
			// (e.g. component/wire ops that carry only a TargetId), from the tracked instance — so
			// CollectionId / TargetTypeId are filled consistently and the materialized doc keeps its _cid.
			object? target = soc.TargetObject;
			TrackedObject? tracked = null;
			if (target is not null)
			{
				tracked = _byModel.GetValue(target, _ => throw new InvalidOperationException("Target object is not attached."));
			}
			else if (soc.TargetId != default && _byId.TryGetValue(soc.TargetId, out var byId))
			{
				target = byId;
				_byModel.TryGetValue(byId, out tracked);
			}
			if (tracked is not null)
			{
				if (soc.TargetId == default)
				{
					soc.TargetId = tracked.Id;
				}
				if (soc.CollectionId == default)
				{
					soc.CollectionId = tracked.CollectionId;
				}
				if (soc.TargetTypeId == default && target is not null)
				{
					soc.TargetTypeId = TypeMetadataProvider.GetTypeMetadata(target.GetType()).TypeId;
				}
			}
		}
		return ProcessCommandAsync(cmd);
	}

	async Task ProcessCommandAsync(Command cmd)
	{
		var ctx = new CommandHandlerContext();
		await cmd.AcceptAsync(this, ctx);
		foreach (var ev in ctx.Events)
		{
			await ev.AcceptAsync(this, null);
		}
		if (_eventStorage is not null && ctx.Events.Count > 0)
		{
			// The materialized documents are the durable source of truth for object/component state, so
			// the event log doesn't need the live CLR payloads that MongoDB's ObjectSerializer can't take:
			// command events are dropped entirely, and a ComponentAddedEvent's Data (the live component)
			// is cleared — the component's full state is already materialized into its container's _c array.
			var toStore = ctx.Events.Where(e => e is not CommandCreatedEvent).ToList();
			foreach (var ev in toStore)
			{
				if (ev is ComponentAddedEvent componentAdded)
				{
					componentAdded.Data = null;
				}
			}
			await _eventStorage.AppendBatchAsync(toStore);
		}
	}

	// ---------------------------------------------------------------- tracking (internal)

	internal Guid Attach(object model, Guid collectionId)
	{
		var id = GuidExtensions.CreateVersion7();
		AttachWithId(model, id, collectionId);
		return id;
	}

	internal void AttachWithId(object model, Guid id, Guid collectionId)
	{
		if (_byModel.TryGetValue(model, out var existing))
		{
			if (existing.Id != id)
			{
				throw new InvalidOperationException($"Object already attached with a different id ({existing.Id} != {id}).");
			}
			return;
		}
		_byModel.Add(model, new TrackedObject { Id = id, CollectionId = collectionId });
		_byId[id] = model;
		if (model is IBindableModel bindable && bindable.Store is null)
		{
			bindable.Attach(this, collectionId);
		}
	}

	internal bool TryGetTracked(Guid id, out object model) => _byId.TryGetValue(id, out model!);

	// ---------------------------------------------------------------- materialization (internal)

	internal object FromDocument(BsonDocument doc, Type type)
	{
		var clone = (BsonDocument)doc.DeepClone();
		clone.Remove("_id");
		clone.Remove("_cid"); // projection-managed partition key, not part of the model
		clone.Remove("_c");    // components are rehydrated separately (see RehydrateComponents)
		var model = JsonSerializer.Deserialize(clone.ToJson(), type, _jsonSerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize a '{type.Name}' document.");
		return model;
	}

	/// <summary>Rebuild a freshly-read container's components from the document's <c>_c</c> array.</summary>
	internal void RehydrateComponents(object model, BsonDocument doc, Guid containerId, Guid collectionId)
	{
		if (model is not IComponentContainer container || !doc.TryGetValue("_c", out var raw) || raw is not BsonArray array)
		{
			return;
		}
		var containerTypeId = TypeMetadataProvider.GetTypeMetadata(model.GetType()).TypeId;
		foreach (var entry in array.OfType<BsonDocument>())
		{
			var componentType = TypeMetadataProvider.GetTypeMetadata(Guid.Parse(entry["_t"].AsString)).Type;
			var componentDoc = (BsonDocument)entry.DeepClone();
			componentDoc.Remove("_t");
			var component = (IComponent)JsonSerializer.Deserialize(componentDoc.ToJson(), componentType, _jsonSerializerOptions)!;
			container.Components.TryAdd(component);
			if (component is IBindableComponent bindable)
			{
				bindable.AttachToContainer(this, containerId, containerTypeId, collectionId);
			}
		}
	}

	BsonDocument ToDocument(object model, Guid id, Guid collectionId)
	{
		var json = JsonSerializer.Serialize(model, model.GetType(), _jsonSerializerOptions);
		var doc = BsonDocument.Parse(json);
		doc["_id"] = id.ToString();
		doc["_cid"] = collectionId.ToString();
		if (model is IComponentContainer container)
		{
			doc.Remove("components"); // a read-only snapshot some containers expose; _c is authoritative
			doc["_c"] = BuildComponentsArray(container);
		}
		return doc;
	}

	BsonArray BuildComponentsArray(IComponentContainer container)
	{
		var array = new BsonArray();
		foreach (var component in container.Components)
		{
			var componentDoc = BsonDocument.Parse(JsonSerializer.Serialize(component, component.GetType(), _jsonSerializerOptions));
			componentDoc["_t"] = TypeMetadataProvider.GetTypeMetadata(component.GetType()).TypeId.ToString();
			array.Add(componentDoc);
		}
		return array;
	}

	void Upsert(Guid targetTypeId, Guid collectionId, Guid id, object model)
	{
		var type = TypeMetadataProvider.GetTypeMetadata(targetTypeId).Type;
		var collection = _database.GetCollection<BsonDocument>(MongoCollectionName(type));
		var doc = ToDocument(model, id, collectionId);
		collection.ReplaceOne(Builders<BsonDocument>.Filter.Eq("_id", id.ToString()), doc, new ReplaceOptions { IsUpsert = true });
	}

	void MarkApplied(Guid targetId, Guid eventId)
	{
		if (_byId.TryGetValue(targetId, out var model) && _byModel.TryGetValue(model, out var tracked))
		{
			tracked.LastEventId = eventId;
		}
	}

	// ---------------------------------------------------------------- ICommandVisitor

	public Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;

	public Task VisitAsync(CreateObjectCommand cmd, CommandHandlerContext ctx)
	{
		ctx.Events.Add(new ObjectCreatedEvent
		{
			StreamId = cmd.StreamId,
			EventId = GuidExtensions.CreateVersion7(),
			CollectionId = cmd.CollectionId,
			CommandId = cmd.CommandId,
			TargetTypeId = cmd.TargetTypeId,
			TargetId = cmd.TargetId,
			DataObject = cmd.TargetObject,
		});

		// Seed each non-default property as a change event so the materialized document carries the
		// initial values (and a fresh projection can rebuild them by replay if the doc is ever dropped).
		foreach (var pi in cmd.Data.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
		{
			var value = pi.GetValue(cmd.Data);
			if (value is null || Equals(value, pi.PropertyType.GetDefault()))
			{
				continue;
			}
			ctx.Events.Add(new ObjectPropertyChangedEvent
			{
				StreamId = cmd.StreamId,
				CommandId = cmd.CommandId,
				CollectionId = cmd.CollectionId,
				EventId = GuidExtensions.CreateVersion7(),
				TargetTypeId = cmd.TargetTypeId,
				TargetId = cmd.TargetId,
				PropertyName = pi.Name,
				OldValue = null,
				NewValue = value,
			});
		}
		return Task.CompletedTask;
	}

	public Task VisitAsync(ChangeObjectPropertyCommand cmd, CommandHandlerContext ctx)
	{
		ctx.Events.Add(new ObjectPropertyChangedEvent
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
		});
		return Task.CompletedTask;
	}

	public Task VisitAsync(DeleteObjectCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;

	public Task VisitAsync(AddComponentCommand cmd, CommandHandlerContext ctx)
	{
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

	public Task VisitAsync(AddWireCommand cmd, CommandHandlerContext ctx)
	{
		ctx.Events.Add(new WireAddedEvent
		{
			StreamId = cmd.StreamId,
			CommandId = cmd.CommandId,
			EventId = GuidExtensions.CreateVersion7(),
			WireId = cmd.WireId == default ? GuidExtensions.CreateVersion7() : cmd.WireId,
			SourceContainerId = cmd.SourceContainerId,
			SourceComponentTypeId = cmd.SourceComponentTypeId,
			SourceComponentId = cmd.SourceComponentId,
			SourcePortName = cmd.SourcePortName,
			TargetContainerId = cmd.TargetContainerId,
			TargetComponentTypeId = cmd.TargetComponentTypeId,
			TargetComponentId = cmd.TargetComponentId,
			TargetPortName = cmd.TargetPortName,
			Type = cmd.Type,
		});
		return Task.CompletedTask;
	}

	public Task VisitAsync(DeleteWireCommand cmd, CommandHandlerContext ctx)
	{
		ctx.Events.Add(new WireDeletedEvent
		{
			StreamId = cmd.StreamId,
			CommandId = cmd.CommandId,
			EventId = GuidExtensions.CreateVersion7(),
			WireId = cmd.WireId,
		});
		return Task.CompletedTask;
	}

	// ---------------------------------------------------------------- IEventVisitor

	public Task BeforeVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;

	public Task VisitAsync(ObjectCreatedEvent ev, EventVisitorContext ctx)
	{
		var type = TypeMetadataProvider.GetTypeMetadata(ev.TargetTypeId).Type;
		object model;
		if (ev.DataObject is not null)
		{
			model = ev.DataObject;
		}
		else if (TryGetTracked(ev.TargetId, out var tracked))
		{
			model = tracked;
		}
		else
		{
			model = Activator.CreateInstance(type)!;
		}

		AttachWithId(model, ev.TargetId, ev.CollectionId);
		Upsert(ev.TargetTypeId, ev.CollectionId, ev.TargetId, model);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	public Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx)
	{
		if (!TryGetTracked(ev.TargetId, out var model))
		{
			throw new InvalidOperationException($"Cannot apply a property change to unknown object {ev.TargetId}.");
		}
		if (model is IBindableModel bindable)
		{
			bindable.Set(ev.PropertyName, ev.NewValue);
		}
		else
		{
			var pi = model.GetType().GetProperty(ev.PropertyName) ?? throw new InvalidOperationException($"Property '{ev.PropertyName}' not found.");
			pi.SetValue(model, ev.NewValue);
		}
		Upsert(ev.TargetTypeId, ev.CollectionId, ev.TargetId, model);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;

	public Task VisitAsync(ComponentAddedEvent ev, EventVisitorContext ctx)
	{
		var container = ResolveContainer(ev.TargetId);
		var componentType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		var activator = RequireActivator();
		var component = activator.Materialize(componentType, ev.Data);

		if (!container.Components.TryAdd(component))
		{
			throw new InvalidOperationException(
				$"ComponentAddedEvent {ev.EventId} could not attach a '{componentType.Name}' to container {ev.TargetId} — uniqueness or veto check rejected it. The event stream is inconsistent.");
		}

		if (component is IBindableComponent bindableComponent)
		{
			bindableComponent.AttachToContainer(this, ev.TargetId, ev.TargetTypeId, ev.CollectionId);
		}

		activator.Activate(component, container, ev.TargetId, isReplay: ctx is not null && ctx.IsReplay);

		Upsert(ev.TargetTypeId, ev.CollectionId, ev.TargetId, container);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	ISynqraComponentActivator RequireActivator()
		=> _componentActivator ?? throw new InvalidOperationException(
			"MongoProjection needs an ISynqraComponentActivator for component events — register it via AddSynqraComponentActivator() (AddMongoDbSynqraStore does this).");

	public Task VisitAsync(ComponentPropertyChangedEvent ev, EventVisitorContext ctx)
	{
		var container = ResolveContainer(ev.TargetId);
		var component = ResolveComponent(container, ev);

		if (component is IBindableModel bindable)
		{
			bindable.Set(ev.PropertyName, ev.NewValue);
		}
		else
		{
			var pi = component.GetType().GetProperty(ev.PropertyName)
				?? throw new InvalidOperationException($"Component '{component.GetType().Name}' has no property '{ev.PropertyName}'.");
			var value = ev.NewValue;
			if (value is IConvertible c)
			{
				value = c.ToType(pi.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
			}
			pi.SetValue(component, value);
		}

		Upsert(ev.TargetTypeId, ev.CollectionId, ev.TargetId, container);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx)
	{
		var container = ResolveContainer(ev.TargetId);
		var component = ResolveComponent(container, ev);

		if (!container.Components.BypassRemove(component))
		{
			throw new InvalidOperationException(
				$"ComponentDeletedEvent {ev.EventId}: component instance was located but the collection refused to remove it. The event stream is inconsistent.");
		}

		Upsert(ev.TargetTypeId, ev.CollectionId, ev.TargetId, container);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	public Task VisitAsync(WireAddedEvent ev, EventVisitorContext ctx)
	{
		var wire = new Wire
		{
			Id = ev.WireId,
			SourceContainerId = ev.SourceContainerId,
			SourceComponentTypeId = ev.SourceComponentTypeId,
			SourceComponentId = ev.SourceComponentId,
			SourcePortName = ev.SourcePortName,
			TargetContainerId = ev.TargetContainerId,
			TargetComponentTypeId = ev.TargetComponentTypeId,
			TargetComponentId = ev.TargetComponentId,
			TargetPortName = ev.TargetPortName,
			Type = ev.Type,
		};
		_wiresById[wire.Id] = wire;
		lock (_wireIndexLock)
		{
			_wiresFrom.GetOrAdd(wire.Source, _ => new List<Wire>()).Add(wire);
			_wiresTo.GetOrAdd(wire.Target, _ => new List<Wire>()).Add(wire);
		}

		var doc = new BsonDocument
		{
			["_id"] = wire.Id.ToString(),
			["sourceContainerId"] = wire.SourceContainerId.ToString(),
			["sourceComponentTypeId"] = wire.SourceComponentTypeId.ToString(),
			["sourceComponentId"] = wire.SourceComponentId.ToString(),
			["sourcePortName"] = wire.SourcePortName ?? (BsonValue)BsonNull.Value,
			["targetContainerId"] = wire.TargetContainerId.ToString(),
			["targetComponentTypeId"] = wire.TargetComponentTypeId.ToString(),
			["targetComponentId"] = wire.TargetComponentId.ToString(),
			["targetPortName"] = wire.TargetPortName ?? (BsonValue)BsonNull.Value,
			["type"] = Convert.ToString(wire.Type) ?? string.Empty,
		};
		_database.GetCollection<BsonDocument>(WiresCollectionName)
			.ReplaceOne(Builders<BsonDocument>.Filter.Eq("_id", wire.Id.ToString()), doc, new ReplaceOptions { IsUpsert = true });
		return Task.CompletedTask;
	}

	public Task VisitAsync(WireDeletedEvent ev, EventVisitorContext ctx)
	{
		if (_wiresById.TryRemove(ev.WireId, out var wire))
		{
			lock (_wireIndexLock)
			{
				if (_wiresFrom.TryGetValue(wire.Source, out var fromList))
				{
					fromList.RemoveAll(w => w.Id == wire.Id);
				}
				if (_wiresTo.TryGetValue(wire.Target, out var toList))
				{
					toList.RemoveAll(w => w.Id == wire.Id);
				}
			}
		}
		_database.GetCollection<BsonDocument>(WiresCollectionName)
			.DeleteOne(Builders<BsonDocument>.Filter.Eq("_id", ev.WireId.ToString()));
		return Task.CompletedTask;
	}

	public Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;

	/// <summary>All wires the projection currently knows about.</summary>
	public IReadOnlyCollection<Wire> Wires => (IReadOnlyCollection<Wire>)_wiresById.Values;

	/// <summary>Wires departing the given source port.</summary>
	public IReadOnlyList<Wire> GetWiresFrom(PortRef source)
	{
		lock (_wireIndexLock)
		{
			return _wiresFrom.TryGetValue(source, out var list) ? list.ToArray() : Array.Empty<Wire>();
		}
	}

	/// <summary>Wires arriving at the given target port.</summary>
	public IReadOnlyList<Wire> GetWiresTo(PortRef target)
	{
		lock (_wireIndexLock)
		{
			return _wiresTo.TryGetValue(target, out var list) ? list.ToArray() : Array.Empty<Wire>();
		}
	}

	// ---------------------------------------------------------------- component apply helpers

	IComponentContainer ResolveContainer(Guid targetId)
	{
		if (!TryGetTracked(targetId, out var model))
		{
			throw new InvalidOperationException($"Container {targetId} not found while applying component event.");
		}
		if (model is not IComponentContainer container)
		{
			throw new InvalidOperationException($"Object {targetId} is a '{model.GetType().Name}' which does not implement IComponentContainer.");
		}
		return container;
	}

	IComponent ResolveComponent(IComponentContainer container, SingleObjectEvent ev)
	{
		var componentType = TypeMetadataProvider.GetTypeMetadata(GetComponentTypeId(ev)).Type;
		var componentId = GetComponentId(ev);

		if (componentId != Guid.Empty)
		{
			foreach (var c in container.Components)
			{
				if (c is IIdentifiable<Guid> identifiable && identifiable.Id == componentId)
				{
					return c;
				}
			}
			throw new InvalidOperationException($"Component {componentId} of type '{componentType.Name}' not found on container.");
		}

		var unique = container.Components.GetUniqueComponent(componentType);
		if (unique is null)
		{
			throw new InvalidOperationException($"No unique-component slot for '{componentType.Name}' is filled on this container.");
		}
		return unique;
	}

	static Guid GetComponentTypeId(SingleObjectEvent ev) => ev switch
	{
		ComponentPropertyChangedEvent p => p.ComponentTypeId,
		ComponentDeletedEvent d => d.ComponentTypeId,
		_ => throw new InvalidOperationException($"Unsupported component event type: {ev.GetType().Name}"),
	};

	static Guid GetComponentId(SingleObjectEvent ev) => ev switch
	{
		ComponentPropertyChangedEvent p => p.ComponentId,
		ComponentDeletedEvent d => d.ComponentId,
		_ => throw new InvalidOperationException($"Unsupported component event type: {ev.GetType().Name}"),
	};

}
