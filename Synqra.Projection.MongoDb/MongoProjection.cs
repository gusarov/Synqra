using System.Collections.Concurrent;
using System.Globalization;
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
/// state as documents in MongoDB — one collection per Synqra collection, one document per object keyed
/// by the object's id (<c>_id</c>).
/// <para>
/// Unlike the in-memory projection (which rebuilds RAM state by replaying the whole log on startup), the
/// materialized read-model is itself durable, so a restarted projection serves current state straight
/// from Mongo with no replay. Documents are stored as the model's JSON shape (round-tripped through the
/// registered <see cref="JsonSerializerOptions"/>), which keeps them queryable and independent of CLR
/// binary layout.
/// </para>
/// <para>
/// <b>Tracking is per-process, not pre-warmed.</b> <see cref="GetId"/>, <see cref="ResolveObject"/>,
/// component mutation, and (for links) endpoint navigation only see objects this projection instance has
/// already touched (created, or loaded via enumerating a <see cref="GetCollection{T}"/>) — there is no
/// startup replay that pre-populates the tracking cache. This is an existing, deliberate limitation (see
/// <see cref="VisitAsync(ObjectPropertyChangedEvent, EventVisitorContext)"/>, which has always thrown for
/// an untracked target) that components and links below intentionally match rather than work around, so
/// behaviour stays consistent across everything this projection materializes. Link <i>queries</i>
/// (<see cref="ILinkIndex"/>) are the exception — they always hit Mongo directly, since adjacency lookups
/// need to work regardless of what this process has touched.
/// </para>
/// </summary>
public sealed class MongoProjection : IObjectStore, IProjection, ILinkIndex
{
	static MongoProjection()
	{
		AppContext.SetSwitch("Synqra.GuidExtensions.ValidateNamespaceIdHashChain", false);
	}

	const string LinksMongoCollectionName = "Links";
	const string LinkTypeIdField = "_linkTypeId";

	readonly IMongoDatabase _database;
	readonly ISbxSerializerFactory _serializerFactory;
	readonly IAppendStorage? _eventStorage;
	readonly JsonSerializerOptions _jsonSerializerOptions;
	readonly IServiceProvider? _serviceProvider;

	// Tracking: model instance <-> id, so generated setters can route ChangeObjectPropertyCommands and
	// GetId() resolves a live instance to its key. A strong id->model map keeps tracked instances alive
	// for the projection's lifetime (acceptable for the v1 store; a weak/eviction policy comes later).
	readonly ConditionalWeakTable<object, TrackedObject> _byModel = new();
	readonly ConcurrentDictionary<Guid, object> _byId = new();
	readonly Dictionary<Guid, MongoStoreCollection> _collections = new();
	readonly object _collectionsGate = new();

	public ITypeMetadataProvider TypeMetadataProvider { get; }

	public Guid StreamId => SynqraGuids.SynqraRootStreamId;

	public MongoProjection(
		  IMongoDatabase database
		, ISbxSerializerFactory serializerFactory
		, ITypeMetadataProvider typeMetadataProvider
		, IAppendStorage<Event, Guid>? eventStorage = null
		, JsonSerializerOptions? jsonSerializerOptions = null
		, JsonSerializerContext? jsonSerializerContext = null
		, IServiceProvider? serviceProvider = null
		)
	{
		_database = database ?? throw new ArgumentNullException(nameof(database));
		_serializerFactory = serializerFactory;
		TypeMetadataProvider = typeMetadataProvider;
		_eventStorage = eventStorage;
		_jsonSerializerOptions = jsonSerializerOptions ?? throw new ArgumentException("MongoProjection requires JsonSerializerOptions to materialize documents.", nameof(jsonSerializerOptions));
		_serviceProvider = serviceProvider;

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
			var mongo = _database.GetCollection<BsonDocument>(MongoCollectionName(type, collectionName));
			var gtype = typeof(MongoStoreCollection<>).MakeGenericType(type);
			var created = (MongoStoreCollection)Activator.CreateInstance(gtype, this, StreamId, collectionId, _serializerFactory, mongo)!;
			_collections[collectionId] = created;
			return created;
		}
	}

	static string MongoCollectionName(Type type, string collectionName)
		=> string.IsNullOrEmpty(collectionName) ? type.Name : $"{type.Name}_{collectionName}";

	public Guid GetId(object model)
	{
		if (_byModel.TryGetValue(model, out var tracked))
		{
			return tracked.Id;
		}
		throw new InvalidOperationException("The object is not attached to this MongoProjection.");
	}

	/// <summary>
	/// Resolves a tracked instance by id, or null. Tracked-only — see the type-level remarks on why
	/// this projection does not fall back to a cross-collection Mongo lookup (there is no registry
	/// mapping an arbitrary id to which per-type Mongo collection holds it). A caller that needs an
	/// id resolved after a restart must first load it into tracking, e.g. by enumerating
	/// <see cref="GetCollection{T}"/> for its type — exactly what <see cref="GetId"/> already requires.
	/// </summary>
	public object? ResolveObject(Guid id) => id == default ? null : (TryGetTracked(id, out var model) ? model : null);

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
		if (cmd is SingleObjectCommand soc && soc.TargetObject is not null)
		{
			var tracked = _byModel.GetValue(soc.TargetObject, _ => throw new InvalidOperationException("Target object is not attached."));
			if (soc.TargetId == default)
			{
				soc.TargetId = tracked.Id;
			}
			if (soc.CollectionId == default)
			{
				soc.CollectionId = tracked.CollectionId;
			}
			var typeId = TypeMetadataProvider.GetTypeMetadata(soc.TargetObject.GetType()).TypeId;
			if (soc.TargetTypeId == default)
			{
				soc.TargetTypeId = typeId;
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
			// Only domain events are durable — command events carry live CLR payloads we don't persist.
			await _eventStorage.AppendBatchAsync(ctx.Events.Where(e => e is not CommandCreatedEvent).ToList());
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
		// A freshly-created container has nothing in the Components collection yet (the query just
		// comes back empty) — this only does real work when re-attaching a container loaded from an
		// existing document (MongoStoreCollection<T>'s enumerator), which is the only place a
		// container's components need rehydrating from where they're actually persisted (see
		// LoadComponentsInto's remarks on why that isn't the container's own document).
		if (model is IComponentContainer container)
		{
			LoadComponentsInto(container, id, collectionId);
		}
	}

	internal bool TryGetTracked(Guid id, out object model) => _byId.TryGetValue(id, out model!);

	internal IMongoCollection<BsonDocument> MongoCollectionFor(Type type, string collectionName)
		=> _database.GetCollection<BsonDocument>(MongoCollectionName(type, collectionName));

	internal object FromDocument(BsonDocument doc, Type type)
	{
		var clone = (BsonDocument)doc.DeepClone();
		clone.Remove("_id");
		clone.Remove(LinkTypeIdField); // present only on Links documents; harmless no-op otherwise
		var model = JsonSerializer.Deserialize(clone.ToJson(), type, _jsonSerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize a '{type.Name}' document.");
		return model;
	}

	BsonDocument ToDocument(object model, Guid id)
	{
		var json = JsonSerializer.Serialize(model, model.GetType(), _jsonSerializerOptions);
		var doc = BsonDocument.Parse(json);
		doc["_id"] = id.ToString();
		return doc;
	}

	void Upsert(Guid targetTypeId, Guid id, object model)
	{
		var type = TypeMetadataProvider.GetTypeMetadata(targetTypeId).Type;
		var collection = _database.GetCollection<BsonDocument>(type.Name);
		var doc = ToDocument(model, id);
		collection.ReplaceOne(Builders<BsonDocument>.Filter.Eq("_id", id.ToString()), doc, new ReplaceOptions { IsUpsert = true });
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
		// Uniqueness / veto checks happen during event apply (where the live ComponentsCollection
		// lives) — same pattern as ChangeObjectProperty.
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
		// Structural dedup happens during event apply (queried straight from Mongo, since link
		// queries always hit Mongo directly — see the type-level remarks) — same pattern as
		// AddComponentCommand's uniqueness check.
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
		Upsert(ev.TargetTypeId, ev.TargetId, model);
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
		Upsert(ev.TargetTypeId, ev.TargetId, model);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	void MarkApplied(Guid targetId, Guid eventId)
	{
		if (_byId.TryGetValue(targetId, out var model) && _byModel.TryGetValue(model, out var tracked))
		{
			tracked.LastEventId = eventId;
		}
	}

	public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;

	public Task VisitAsync(ComponentAddedEvent ev, EventVisitorContext ctx)
	{
		var container = ResolveContainer(ev.TargetId);

		var componentType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		var component = ComponentApplyHelpers.MaterializeComponent(componentType, ev.Data);

		if (!container.Components.TryAdd(component))
		{
			throw new InvalidOperationException(
				$"ComponentAddedEvent {ev.EventId} could not attach a '{componentType.Name}' to container {ev.TargetId} — uniqueness or veto check rejected it during replay. The event stream is inconsistent.");
		}

		// Wire up the container linkage so the component's generated property setters can build
		// ChangeComponentPropertyCommands without the caller threading the container reference.
		if (component is IBindableComponent bindableComponent)
		{
			bindableComponent.AttachToContainer(this, ev.TargetId, ev.TargetTypeId, ev.CollectionId);
		}

		// MongoProjection never replays (its documents ARE the durable state — see the type-level
		// remarks), so every ComponentAddedEvent it applies is, by definition, an originating one.
		if (component is IActivatableComponent activatable && _serviceProvider is not null)
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

		UpsertComponent(ev.TargetId, ev.ComponentTypeId, ev.ComponentId, component);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	public Task VisitAsync(ComponentPropertyChangedEvent ev, EventVisitorContext ctx)
	{
		var container = ResolveContainer(ev.TargetId);
		var component = ComponentApplyHelpers.ResolveComponent(container, ev, TypeMetadataProvider);

		// Reuse the bindable-model set path so listeners (INotifyPropertyChanged etc.) fire
		// naturally. Components that don't implement IBindableModel fall back to reflection.
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
				value = c.ToType(pi.PropertyType, CultureInfo.InvariantCulture);
			}
			pi.SetValue(component, value);
		}

		UpsertComponent(ev.TargetId, ev.ComponentTypeId, ev.ComponentId, component);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx)
	{
		var container = ResolveContainer(ev.TargetId);
		var component = ComponentApplyHelpers.ResolveComponent(container, ev, TypeMetadataProvider);

		// BypassRemove rather than Remove: when the container is wrapped in
		// StoreBoundComponentsCollection, the ICollection<T>.Remove path emits a command. The
		// projection is APPLYING an event, so it must skip the command channel — otherwise it would
		// generate a recursive delete command.
		if (!container.Components.BypassRemove(component))
		{
			throw new InvalidOperationException(
				$"ComponentDeletedEvent {ev.EventId}: component instance was located but the collection refused to remove it. The event stream is inconsistent.");
		}

		DeleteComponentDoc(ev.TargetId, ev.ComponentTypeId, ev.ComponentId);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	public Task VisitAsync(LinkAddedEvent ev, EventVisitorContext ctx)
	{
		var linkType = TypeMetadataProvider.GetTypeMetadata(ev.LinkTypeId).Type;
		var link = LinkApplyHelpers.MaterializeLink(linkType, ev.Data);
		link.LinkId = ev.LinkId == default ? GuidExtensions.CreateVersion7() : ev.LinkId;
		// SourceId/TargetId are mandatory, explicit fields on the event (see AddLinkCommand's
		// remarks) — authoritative over whatever Data happened to carry.
		link.SourceId = ev.SourceId;
		link.TargetId = ev.TargetId;

		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		if (FindStructuralDuplicate(linksMongo, link, link.LinkId) is { } duplicateId)
		{
			throw new InvalidOperationException(
				$"LinkAddedEvent {ev.EventId} could not register a '{linkType.Name}' link — an equivalent link ({duplicateId}) already exists between the same endpoints.");
		}

		var doc = ToDocument(link, link.LinkId);
		doc[LinkTypeIdField] = ev.LinkTypeId.ToString();
		linksMongo.ReplaceOne(Builders<BsonDocument>.Filter.Eq("_id", link.LinkId.ToString()), doc, new ReplaceOptions { IsUpsert = true });

		if (link is IBindableModel bindable && bindable.Store is null)
		{
			bindable.Attach(this, TypeMetadataProvider.GetTypeMetadata(linkType).GetCollectionId(""));
		}
		return Task.CompletedTask;
	}

	public Task VisitAsync(LinkRemovedEvent ev, EventVisitorContext ctx)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		linksMongo.DeleteOne(Builders<BsonDocument>.Filter.Eq("_id", ev.LinkId.ToString())); // no-op (idempotent) if already gone
		return Task.CompletedTask;
	}

	/// <summary>
	/// Looks for an existing link with the same <see cref="Link.StructuralKey"/> as <paramref name="link"/>,
	/// excluding <paramref name="excludeId"/> itself. Queries Mongo directly for the candidate set (both
	/// endpoint orders, since an undirected duplicate can arrive reversed) and decides structural equality
	/// in memory via <see cref="Link.StructuralKey"/> — sidesteps needing to translate <see cref="LinkKey"/>
	/// into a Mongo filter.
	/// </summary>
	Guid? FindStructuralDuplicate(IMongoCollection<BsonDocument> linksMongo, Link link, Guid excludeId)
	{
		var typeIdFilter = Builders<BsonDocument>.Filter.Eq(LinkTypeIdField, TypeMetadataProvider.GetTypeMetadata(link.GetType()).TypeId.ToString());
		// SourceId/TargetId are stored as strings — ToDocument round-trips the model through
		// JsonSerializer, which renders a Guid as a string, not the BSON Binary subtype the driver's
		// own GuidSerializer would produce for a typed Guid filter value. Compare as strings to match.
		var endpointFilter = Builders<BsonDocument>.Filter.Or(
			Builders<BsonDocument>.Filter.And(
				Builders<BsonDocument>.Filter.Eq("SourceId", link.SourceId.ToString()),
				Builders<BsonDocument>.Filter.Eq("TargetId", link.TargetId.ToString())),
			Builders<BsonDocument>.Filter.And(
				Builders<BsonDocument>.Filter.Eq("SourceId", link.TargetId.ToString()),
				Builders<BsonDocument>.Filter.Eq("TargetId", link.SourceId.ToString())));

		foreach (var doc in linksMongo.Find(Builders<BsonDocument>.Filter.And(typeIdFilter, endpointFilter)).ToList())
		{
			var candidateId = Guid.Parse(doc["_id"].AsString);
			if (candidateId == excludeId)
			{
				continue;
			}
			var candidate = (Link)FromDocument(doc, link.GetType());
			if (candidate.StructuralKey.Equals(link.StructuralKey))
			{
				return candidateId;
			}
		}
		return null;
	}

	public Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;

	// ---------------------------------------------------------------- Component apply helpers

	IComponentContainer ResolveContainer(Guid targetId)
	{
		TryGetTracked(targetId, out var model);
		return ComponentApplyHelpers.ResolveContainer(model, targetId);
	}

	const string ComponentsMongoCollectionName = "Components";

	/// <summary>
	/// Components are persisted in their own shared Mongo collection — NOT embedded in their
	/// container's own document the way the container's plain properties are. <see cref="IComponentsCollection"/>'s
	/// element type is the marker interface <see cref="IComponent"/>, and <c>System.Text.Json</c>
	/// serializes a collection element using its <i>declared</i> element type, not its runtime
	/// type — so round-tripping the container's own <c>Components</c> property as embedded JSON
	/// loses every component-specific field (confirmed empirically: the document came back as
	/// <c>"Components": [{ }]</c>, an empty object, for a component that very much had properties).
	/// Serializing each component on its own, via its own concrete type
	/// (<c>JsonSerializer.Serialize(component, component.GetType(), ...)</c>), sidesteps the
	/// polymorphism gap entirely — no declared-interface element type is ever involved. The
	/// (container, type, id) triple this document is filtered on mirrors exactly how
	/// <see cref="ComponentApplyHelpers.ResolveComponent"/> already addresses a component for the live, in-memory side
	/// of this same apply path.
	/// </summary>
	static FilterDefinition<BsonDocument> ComponentFilter(Guid containerId, Guid componentTypeId, Guid componentId) =>
		Builders<BsonDocument>.Filter.And(
			Builders<BsonDocument>.Filter.Eq("ContainerId", containerId.ToString()),
			Builders<BsonDocument>.Filter.Eq("ComponentTypeId", componentTypeId.ToString()),
			Builders<BsonDocument>.Filter.Eq("ComponentId", componentId.ToString()));

	void UpsertComponent(Guid containerId, Guid componentTypeId, Guid componentId, IComponent component)
	{
		var componentsMongo = _database.GetCollection<BsonDocument>(ComponentsMongoCollectionName);
		var json = JsonSerializer.Serialize(component, component.GetType(), _jsonSerializerOptions);
		var doc = BsonDocument.Parse(json);
		doc["ContainerId"] = containerId.ToString();
		doc["ComponentTypeId"] = componentTypeId.ToString();
		doc["ComponentId"] = componentId.ToString();
		componentsMongo.ReplaceOne(ComponentFilter(containerId, componentTypeId, componentId), doc, new ReplaceOptions { IsUpsert = true });
	}

	void DeleteComponentDoc(Guid containerId, Guid componentTypeId, Guid componentId)
		=> _database.GetCollection<BsonDocument>(ComponentsMongoCollectionName).DeleteOne(ComponentFilter(containerId, componentTypeId, componentId));

	/// <summary>
	/// Re-attaches every persisted component onto a container as it's (re)loaded — called from
	/// <see cref="AttachWithId"/>, so it runs for a container coming back out of Mongo via
	/// <c>MongoStoreCollection&lt;T&gt;</c>'s enumerator (a brand new container has nothing to find
	/// here; the query just comes back empty). Deliberately skips <see cref="IActivatableComponent"/>
	/// activation: this is rehydration of pre-existing state, not an originating add — the same
	/// distinction <see cref="VisitAsync(ComponentAddedEvent, EventVisitorContext)"/> draws via
	/// <see cref="ComponentActivationContext.IsReplay"/>, just reached by a different path (Mongo
	/// has no event-log replay at all, so there's no IsReplay flag already in flight to reuse here).
	/// </summary>
	void LoadComponentsInto(IComponentContainer container, Guid containerId, Guid containerCollectionId)
	{
		var componentsMongo = _database.GetCollection<BsonDocument>(ComponentsMongoCollectionName);
		foreach (var doc in componentsMongo.Find(Builders<BsonDocument>.Filter.Eq("ContainerId", containerId.ToString())).ToList())
		{
			var componentTypeId = Guid.Parse(doc["ComponentTypeId"].AsString);
			var componentType = TypeMetadataProvider.GetTypeMetadata(componentTypeId).Type;

			var clone = (BsonDocument)doc.DeepClone();
			clone.Remove("_id");
			clone.Remove("ContainerId");
			clone.Remove("ComponentTypeId");
			clone.Remove("ComponentId");
			var component = (IComponent)(JsonSerializer.Deserialize(clone.ToJson(), componentType, _jsonSerializerOptions)
				?? throw new InvalidOperationException($"Failed to deserialize a '{componentType.Name}' component."));

			if (!container.Components.TryAdd(component))
			{
				continue; // defensive only — a freshly-deserialized component can't already violate uniqueness
			}
			if (component is IBindableComponent bindableComponent)
			{
				bindableComponent.AttachToContainer(this, containerId, TypeMetadataProvider.GetTypeMetadata(container.GetType()).TypeId, containerCollectionId);
			}
		}
	}

	// ---------------------------------------------------------------- ILinkIndex

	/// <summary>
	/// Deserializes a link document and attaches it to this store — every link <see cref="ILinkIndex"/>
	/// hands back must be attached, or its typed <c>Source</c>/<c>Target</c> accessors (which resolve
	/// through <see cref="IBindableModel.Store"/>) would silently return null.
	/// </summary>
	Link LoadLink(BsonDocument doc, Type linkType)
	{
		var link = (Link)FromDocument(doc, linkType);
		if (link is IBindableModel bindable && bindable.Store is null)
		{
			bindable.Attach(this, TypeMetadataProvider.GetTypeMetadata(linkType).GetCollectionId(""));
		}
		return link;
	}

	IReadOnlyCollection<Link> ILinkIndex.Links
	{
		get
		{
			var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
			var result = new List<Link>();
			foreach (var doc in linksMongo.Find(FilterDefinition<BsonDocument>.Empty).ToList())
			{
				var linkTypeId = Guid.Parse(doc[LinkTypeIdField].AsString);
				var linkType = TypeMetadataProvider.GetTypeMetadata(linkTypeId).Type;
				result.Add(LoadLink(doc, linkType));
			}
			return result;
		}
	}

	IReadOnlyList<Link> ILinkIndex.LinksAt(Guid nodeId, LinkEnd end, Type linkType)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var typeIdFilter = Builders<BsonDocument>.Filter.Eq(LinkTypeIdField, TypeMetadataProvider.GetTypeMetadata(linkType).TypeId.ToString());
		// Compared as strings — see FindStructuralDuplicate's remark on why.
		var nodeIdString = nodeId.ToString();
		var endpointFilter = end switch
		{
			LinkEnd.None => throw new ArgumentException("LinkEnd.None is not a valid link end.", nameof(end)),
			LinkEnd.Source => Builders<BsonDocument>.Filter.Eq("SourceId", nodeIdString),
			LinkEnd.Target => Builders<BsonDocument>.Filter.Eq("TargetId", nodeIdString),
			_ => Builders<BsonDocument>.Filter.Or(
				Builders<BsonDocument>.Filter.Eq("SourceId", nodeIdString),
				Builders<BsonDocument>.Filter.Eq("TargetId", nodeIdString)),
		};
		return linksMongo.Find(Builders<BsonDocument>.Filter.And(typeIdFilter, endpointFilter)).ToList()
			.Select(doc => LoadLink(doc, linkType))
			.ToArray();
	}

	IReadOnlyList<Link> ILinkIndex.LinksBetween(Guid a, Guid b, Type linkType)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var typeIdFilter = Builders<BsonDocument>.Filter.Eq(LinkTypeIdField, TypeMetadataProvider.GetTypeMetadata(linkType).TypeId.ToString());
		var aString = a.ToString();
		var bString = b.ToString();
		var endpointFilter = Builders<BsonDocument>.Filter.Or(
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", aString), Builders<BsonDocument>.Filter.Eq("TargetId", bString)),
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", bString), Builders<BsonDocument>.Filter.Eq("TargetId", aString)));
		return linksMongo.Find(Builders<BsonDocument>.Filter.And(typeIdFilter, endpointFilter)).ToList()
			.Select(doc => LoadLink(doc, linkType))
			.ToArray();
	}

	bool ILinkIndex.TryGetByKey(LinkKey key, out Link? link)
	{
		var linkType = key.LinkType;
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var typeIdFilter = Builders<BsonDocument>.Filter.Eq(LinkTypeIdField, TypeMetadataProvider.GetTypeMetadata(linkType).TypeId.ToString());
		var xString = key.X.ToString();
		var yString = key.Y.ToString();
		var endpointFilter = Builders<BsonDocument>.Filter.Or(
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", xString), Builders<BsonDocument>.Filter.Eq("TargetId", yString)),
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", yString), Builders<BsonDocument>.Filter.Eq("TargetId", xString)));

		foreach (var doc in linksMongo.Find(Builders<BsonDocument>.Filter.And(typeIdFilter, endpointFilter)).ToList())
		{
			// StructuralKey only depends on SourceId/TargetId/type, all already on the raw document,
			// so a bare FromDocument (no Attach) is enough just to evaluate it — LoadLink is reserved
			// for the actual match, which is what callers keep and navigate from.
			var candidate = (Link)FromDocument(doc, linkType);
			if (candidate.StructuralKey.Equals(key))
			{
				link = LoadLink(doc, linkType);
				return true;
			}
		}
		link = null;
		return false;
	}

	bool ILinkIndex.TryGetById(Guid linkId, out Link? link)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var doc = linksMongo.Find(Builders<BsonDocument>.Filter.Eq("_id", linkId.ToString())).FirstOrDefault();
		if (doc is null)
		{
			link = null;
			return false;
		}
		var linkTypeId = Guid.Parse(doc[LinkTypeIdField].AsString);
		var linkType = TypeMetadataProvider.GetTypeMetadata(linkTypeId).Type;
		link = LoadLink(doc, linkType);
		return true;
	}
}
