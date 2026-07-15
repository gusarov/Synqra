using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Synqra.AppendStorage;
using Synqra.AppendStorage.MongoDb;
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
		// Native BSON (de)serialization below needs the same global conventions the event log
		// registers (drop [JsonIgnore] members, skip nulls) and the same patched Guid/object
		// serializer defaults — register them here too, since a host that wires up MongoDb only for
		// the projection (not the event log) would otherwise never call this. Idempotent/guarded.
		MongoEventClassMaps.Register();
	}

	const string LinksMongoCollectionName = "Links";

	// Links of every concrete type share one collection (so a graph-wide scan is a single query),
	// so each document needs a type marker. That marker is the same "_t" discriminator ToDocument
	// already stamps for polymorphic deserialization — no separate type-id column. The value is the
	// link type's simple name (what ObjectConverter writes), so a per-type query filters on it directly.
	const string DiscriminatorField = "_t";
	// Short, underscore-prefixed system column — matches the "_id"/"_t" naming convention. NOT
	// "_cid" ("ContainerId", the historical Synqra name for this concept — see
	// SynqraGuids.SynqraRootStreamId's own remark): "container" is being phased out of the
	// vocabulary entirely now that Components (see EntityIdField below) also legitimately use that
	// word for something unrelated (IComponentContainer — the object a component is attached to).
	// Stream and node are the two concepts that actually exist; "container" isn't a third one.
	const string StreamIdField = "_sid";
	static FilterDefinition<BsonDocument> LinkTypeFilter(Type linkType)
		=> Builders<BsonDocument>.Filter.Eq(DiscriminatorField, linkType.Name);

	/// <summary>Every read/write below is scoped to this instance's <see cref="StreamId"/> — see
	/// the property's own remarks for why.</summary>
	FilterDefinition<BsonDocument> StreamFilter()
		=> Builders<BsonDocument>.Filter.Eq(StreamIdField, StreamId);

	readonly IMongoDatabase _database;
	readonly ISbxSerializerFactory _serializerFactory;
	readonly IAppendStorage? _eventStorage;
	readonly JsonSerializerOptions _jsonSerializerOptions;
	readonly IServiceProvider? _serviceProvider;

	// null => multitenant (read the ambient scope per call); a value => pinned to that one stream.
	readonly Guid? _pinnedStreamId;

	// Tracking: model instance <-> id, so generated setters can route ChangeObjectPropertyCommands and
	// GetId() resolves a live instance to its key. A strong id->model map keeps tracked instances alive
	// for the projection's lifetime (acceptable for the v1 store; a weak/eviction policy comes later).
	readonly ConditionalWeakTable<object, TrackedObject> _byModel = new();
	readonly ConcurrentDictionary<Guid, object> _byId = new();
	readonly Dictionary<Guid, MongoStoreCollection> _collections = new();
	readonly object _collectionsGate = new();

	public ITypeMetadataProvider TypeMetadataProvider { get; }

	/// <summary>
	/// Every document this projection reads or writes belongs to exactly this stream — see
	/// <see cref="StreamFilter"/>, applied to every Mongo query in this type, and the "_sid"
	/// field <see cref="ToDocument"/>/<see cref="UpsertComponent"/> stamp on every write. A single
	/// physical Mongo database can therefore hold more than one stream's worth of documents
	/// (per-user, per-tenant, whatever the caller's stream boundary is) without them leaking into
	/// each other's queries.
	/// <para>
	/// This store is <b>omnitenant</b>: with no <see cref="StreamPin"/> it is <i>multitenant</i> — it
	/// captures no stream at construction and reads the caller's ambient <see cref="SynqraStreamContext"/>
	/// scope on every access, so one server serves however many concurrent streams it has from a single
	/// instance. A factory that builds a single-tenant instance at the point of use passes an optional
	/// <see cref="StreamPin"/> (deliberately never registered in DI) to pin it to that one stream. See
	/// <see cref="SynqraStreamContext.Resolve"/>.
	/// </para>
	/// </summary>
	public Guid StreamId => SynqraStreamContext.Resolve(_pinnedStreamId);

	public MongoProjection(
		  IMongoDatabase database
		, ISbxSerializerFactory serializerFactory
		, ITypeMetadataProvider typeMetadataProvider
		, IAppendStorage<Event, Guid>? eventStorage = null
		, JsonSerializerOptions? jsonSerializerOptions = null
		, JsonSerializerContext? jsonSerializerContext = null
		, IServiceProvider? serviceProvider = null
		, StreamPin? pin = null
		)
	{
		_database = database ?? throw new ArgumentNullException(nameof(database));
		_serializerFactory = serializerFactory;
		TypeMetadataProvider = typeMetadataProvider;
		_eventStorage = eventStorage;
		_jsonSerializerOptions = jsonSerializerOptions ?? throw new ArgumentException("MongoProjection requires JsonSerializerOptions to materialize documents.", nameof(jsonSerializerOptions));
		_serviceProvider = serviceProvider;

		// No pin => root/multitenant (resolve the ambient scope per call); a StreamPin (supplied only by
		// a factory at the point of use, never by DI) => this instance is pinned to that single stream.
		_pinnedStreamId = pin?.StreamId;

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
		LinkApplyHelpers.GuardNotLinkType(type);
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
		// A command without a stream id inherits this store's stream; one carrying a different stream
		// is a misroute and throws (mirrors File / InMemory).
		var streamId = StreamId;
		if (cmd.StreamId == default)
		{
			cmd.StreamId = streamId;
		}
		else if (cmd.StreamId != streamId)
		{
			throw new InvalidOperationException(
				$"Command stream {cmd.StreamId} does not match this store's stream {streamId} — refusing misrouted command {cmd.CommandId}.");
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

	/// <summary>
	/// Rehydrates a stored document into its concrete model via the native MongoDB driver
	/// serializer — no JSON round-trip. The concrete type is read from the document's own <c>_t</c>
	/// discriminator (written by <see cref="ToDocument"/>) and resolved through
	/// <see cref="SynqraJsonTypeInfoResolver.GetByDiscriminator"/> — the very same discriminator
	/// registry <see cref="ObjectConverter"/> (the JSON side) uses, so both serializers agree on what
	/// <c>_t</c> values mean without the driver's own (class-map-based) polymorphism machinery ever
	/// getting involved. That sidesteps a real cold-start gap: BSON's built-in discriminator
	/// resolution only knows about a type once the driver has auto-mapped it at least once in this
	/// process, which a freshly-started process reading pre-existing documents can't guarantee —
	/// whereas Synqra's own registry is populated by each model's generated static registration, at
	/// type load, independent of Mongo ever having been touched.
	/// <paramref name="addressingFields"/> names the projection's own non-model bookkeeping columns
	/// (e.g. a component's <c>ContainerId</c>) to drop before deserializing, alongside the always-
	/// dropped <c>_t</c> (resolving the type is the only thing <c>_t</c> is for; the concrete type's
	/// own class map has no member for it). <c>_id</c> is dropped too — UNLESS the resolved type is a
	/// <see cref="Link"/>, whose own class map maps <c>_id</c> to the real <c>LinkId</c> member (see
	/// <see cref="MongoEventClassMaps.Register"/>): stripping it there would leave <c>LinkId</c>
	/// un-set on the deserialized instance.
	/// </summary>
	internal object FromDocument(BsonDocument doc, params string[] addressingFields)
	{
		var discriminator = doc["_t"].AsString;
		var type = SynqraJsonTypeInfoResolver.GetByDiscriminator(discriminator)
			?? throw new InvalidOperationException($"Unknown discriminator '{discriminator}' — no model is registered under that name.");

		var clone = (BsonDocument)doc.DeepClone();
		if (!typeof(Link).IsAssignableFrom(type))
		{
			clone.Remove("_id");
		}
		clone.Remove("_t");
		clone.Remove(StreamIdField);
		foreach (var field in addressingFields)
		{
			clone.Remove(field);
		}
		return BsonSerializer.Deserialize(clone, type);
	}

	/// <summary>
	/// Serializes a model to a stored document via the native MongoDB driver serializer — no JSON
	/// round-trip. Going through nominal type <c>object</c> (rather than <c>model.GetType()</c>) means
	/// the driver always sees nominal != actual type, so it always writes the <c>_t</c> discriminator
	/// — even when the value is held behind an interface/base (a component behind <c>IComponent</c>,
	/// a link behind <c>Link</c>) where nominal and actual would otherwise coincide. Every concrete
	/// field is included by construction (this serializes the *actual* type's own class map, not a
	/// declared base/interface's). The globally-registered conventions (see
	/// <see cref="MongoEventClassMaps.Register"/>, called from this class's static constructor) drop
	/// any <c>[JsonIgnore]</c> member from that class map, matching the JSON side's persisted shape.
	/// A type whose own class map already designates an id member (<see cref="Link"/>'s <c>LinkId</c>,
	/// mapped onto <c>_id</c> — see <see cref="MongoEventClassMaps.Register"/>) gets <c>_id</c> written
	/// natively as part of that serialization, so it's left alone here; a plain model with no such
	/// member gets <paramref name="id"/> written as the document's <c>_id</c>, as a native Guid (not a
	/// string) so every id is represented identically across the whole document.
	/// </summary>
	BsonDocument ToDocument(object model, Guid id)
	{
		// Explicit serializer, not ambient BsonSerializer.LookupSerializer(typeof(object)) — the
		// driver's own default ObjectSerializer rejects any concrete type it doesn't recognize as
		// "safe", and a consumer's model type (e.g. a Quotaly feature's Node) is never one ahead of
		// time. See MongoEventClassMaps.ScopedOpenObjectSerializer's own remarks.
		var doc = model.ToBsonDocument(typeof(object), MongoEventClassMaps.ScopedOpenObjectSerializer);
		if (!doc.Contains("_id"))
		{
			doc["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard);
		}
		doc[StreamIdField] = new BsonBinaryData(StreamId, GuidRepresentation.Standard);
		return doc;
	}

	void Upsert(Guid targetTypeId, Guid id, object model)
	{
		var type = TypeMetadataProvider.GetTypeMetadata(targetTypeId).Type;
		var collection = _database.GetCollection<BsonDocument>(type.Name);
		var doc = ToDocument(model, id);
		var filter = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", id), StreamFilter());
		collection.ReplaceOne(filter, doc, new ReplaceOptions { IsUpsert = true });
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
		if (ev.ComponentId == ev.TargetId)
		{
			// Phase 2 (ECS): self-owned ROOT COMPONENT = the entity's own data (_id == _eid == entityId).
			// Track + persist it via the object path (kept in its per-type Mongo collection for now — the
			// physical collection-merge into Components is a later step).
			var rootType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
			object rootModel = ev.Data ?? (TryGetTracked(ev.TargetId, out var tracked) ? tracked : Activator.CreateInstance(rootType)!);
			AttachWithId(rootModel, ev.TargetId, ev.CollectionId);
			Upsert(ev.ComponentTypeId, ev.TargetId, rootModel);
			MarkApplied(ev.TargetId, ev.EventId);
			return Task.CompletedTask;
		}

		var container = ResolveContainer(ev.TargetId);

		var componentType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		var component = ComponentApplyHelpers.MaterializeComponent(componentType, ev.Data, ev.ComponentId);

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
		if (ev.ComponentId == ev.TargetId)
		{
			// Root component property change = the entity's own property change.
			if (!TryGetTracked(ev.TargetId, out var rootModel))
			{
				throw new InvalidOperationException($"Cannot apply a root property change to unknown entity {ev.TargetId}.");
			}
			if (rootModel is IBindableModel rbind)
			{
				rbind.Set(ev.PropertyName, ev.NewValue);
			}
			else
			{
				var rpi = rootModel.GetType().GetProperty(ev.PropertyName)
					?? throw new InvalidOperationException($"Root entity '{rootModel.GetType().Name}' has no property '{ev.PropertyName}'.");
				var rv = ev.NewValue;
				if (rv is IConvertible rc) rv = rc.ToType(rpi.PropertyType, CultureInfo.InvariantCulture);
				rpi.SetValue(rootModel, rv);
			}
			Upsert(ev.ComponentTypeId, ev.TargetId, rootModel);
			MarkApplied(ev.TargetId, ev.EventId);
			return Task.CompletedTask;
		}

		var container = ResolveContainer(ev.TargetId);
		var component = container.ResolveComponent(ev, TypeMetadataProvider);

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
		if (ev.ComponentId == ev.TargetId)
		{
			// Root entity delete — Phase 4; ObjectDeleted is a no-op today, keep parity.
			return Task.CompletedTask;
		}

		var container = ResolveContainer(ev.TargetId);
		var component = container.ResolveComponent(ev, TypeMetadataProvider);

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

		// ToDocument already stamps the "_t" discriminator (= link type name) the per-type queries
		// filter on, so no separate type column is written here.
		var doc = ToDocument(link, link.LinkId);
		var replaceFilter = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", link.LinkId), StreamFilter());
		linksMongo.ReplaceOne(replaceFilter, doc, new ReplaceOptions { IsUpsert = true });

		if (link is IBindableModel bindable && bindable.Store is null)
		{
			bindable.Attach(this, TypeMetadataProvider.GetTypeMetadata(linkType).GetCollectionId(""));
		}
		return Task.CompletedTask;
	}

	public Task VisitAsync(LinkRemovedEvent ev, EventVisitorContext ctx)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var deleteFilter = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", ev.LinkId), StreamFilter());
		linksMongo.DeleteOne(deleteFilter); // no-op (idempotent) if already gone
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
		var typeIdFilter = LinkTypeFilter(link.GetType());
		// SourceId/TargetId are native Link fields, serialized through Link's own class map (the
		// patched standard GuidSerializer) — filter with a raw Guid so the driver encodes it the same
		// way (BSON Binary subtype), not a string.
		var endpointFilter = Builders<BsonDocument>.Filter.Or(
			Builders<BsonDocument>.Filter.And(
				Builders<BsonDocument>.Filter.Eq("SourceId", link.SourceId),
				Builders<BsonDocument>.Filter.Eq("TargetId", link.TargetId)),
			Builders<BsonDocument>.Filter.And(
				Builders<BsonDocument>.Filter.Eq("SourceId", link.TargetId),
				Builders<BsonDocument>.Filter.Eq("TargetId", link.SourceId)));

		foreach (var doc in linksMongo.Find(Builders<BsonDocument>.Filter.And(StreamFilter(), typeIdFilter, endpointFilter)).ToList())
		{
			var candidateId = doc["_id"].AsGuid;
			if (candidateId == excludeId)
			{
				continue;
			}
			var candidate = (Link)FromDocument(doc);
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

	// Short, underscore-prefixed system column, matching "_id"/"_t"/"_sid": the **entity id** — the
	// id of the thing this component belongs to (its IComponentContainer today; the ECS entity under
	// the model substrate). Named "_eid" (entity id), not "_oid" — "OID" collides with the object
	// identifier of the security/X.509 RFCs — and stays in the short-system-column family next to _sid.
	// For a root component the invariant is _id == _eid == entityId.
	const string EntityIdField = "_eid";

	/// <summary>
	/// Components are persisted in their own shared Mongo collection — NOT embedded in their
	/// owner's own document the way the owner's plain properties are. <see cref="IComponentsCollection"/>'s
	/// element type is the marker interface <see cref="IComponent"/>; serialized through that
	/// declared element type, the driver would build a class map for the interface itself (which has
	/// no members) and drop every concrete field. Going through nominal type <c>object</c> (see
	/// <see cref="ToDocument"/>) sidesteps that — the driver serializes the *actual* type's own class
	/// map and always stamps a <c>_t</c> discriminator, and reading back resolves the concrete type
	/// from <c>_t</c> alone via <see cref="FromDocument"/>. So the stored document needs no separate
	/// type-id column — the <c>_t</c> discriminator doubles as the type filter, mirroring how
	/// links are stored. The remaining (owner, type, id) addressing matches how
	/// <see cref="ComponentApplyHelpers.ResolveComponent"/> addresses a component on the live, in-memory
	/// side of this same apply path — with the type filtered via <c>_t</c> so a unique component
	/// (<c>ComponentId == Guid.Empty</c>) is still distinguished from a peer of another type.
	/// <paramref name="containerId"/> is the id of the <see cref="IComponentContainer"/> this component
	/// is attached to (matching that concept's name everywhere else in Synqra) — stored under
	/// <see cref="EntityIdField"/>, not a literal "ContainerId"/"Container" column, per that field's
	/// own remark.
	/// </summary>
	FilterDefinition<BsonDocument> ComponentFilter(Guid containerId, Type componentType, Guid componentId) =>
		Builders<BsonDocument>.Filter.And(
			StreamFilter(),
			Builders<BsonDocument>.Filter.Eq(EntityIdField, containerId),
			Builders<BsonDocument>.Filter.Eq(DiscriminatorField, componentType.Name),
			Builders<BsonDocument>.Filter.Eq("ComponentId", componentId));

	void UpsertComponent(Guid containerId, Guid componentTypeId, Guid componentId, IComponent component)
	{
		var componentsMongo = _database.GetCollection<BsonDocument>(ComponentsMongoCollectionName);
		// Native driver serialization through nominal type object -> always writes "_t" (see ToDocument).
		// Explicit serializer, not ambient lookup — see ToDocument's own remarks.
		var doc = component.ToBsonDocument(typeof(object), MongoEventClassMaps.ScopedOpenObjectSerializer);
		doc[EntityIdField] = new BsonBinaryData(containerId, GuidRepresentation.Standard);
		doc["ComponentId"] = new BsonBinaryData(componentId, GuidRepresentation.Standard);
		doc[StreamIdField] = new BsonBinaryData(StreamId, GuidRepresentation.Standard);
		componentsMongo.ReplaceOne(ComponentFilter(containerId, component.GetType(), componentId), doc, new ReplaceOptions { IsUpsert = true });
	}

	void DeleteComponentDoc(Guid containerId, Guid componentTypeId, Guid componentId)
		=> _database.GetCollection<BsonDocument>(ComponentsMongoCollectionName)
			.DeleteOne(ComponentFilter(containerId, TypeMetadataProvider.GetTypeMetadata(componentTypeId).Type, componentId));

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
		var loadFilter = Builders<BsonDocument>.Filter.And(StreamFilter(), Builders<BsonDocument>.Filter.Eq(EntityIdField, containerId));
		foreach (var doc in componentsMongo.Find(loadFilter).ToList())
		{
			// Concrete type comes from the "_t" discriminator inside the document (see UpsertComponent) —
			// no type-id column to read; FromDocument strips the addressing columns and resolves it.
			var component = (IComponent)FromDocument(doc, EntityIdField, "ComponentId");

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
	Link LoadLink(BsonDocument doc)
	{
		var link = (Link)FromDocument(doc); // concrete type resolved from the "_t" discriminator
		if (link is IBindableModel bindable && bindable.Store is null)
		{
			bindable.Attach(this, TypeMetadataProvider.GetTypeMetadata(link.GetType()).GetCollectionId(""));
		}
		return link;
	}

	IReadOnlyCollection<Link> ILinkIndex.Links
	{
		get
		{
			var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
			return linksMongo.Find(StreamFilter()).ToList()
				.Select(LoadLink)
				.ToArray();
		}
	}

	IReadOnlyList<Link> ILinkIndex.LinksAt(Guid nodeId, LinkEnd end, Type linkType)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		// Native Guid filter — see FindStructuralDuplicate's remark on why.
		var endpointFilter = end switch
		{
			LinkEnd.None => throw new ArgumentException("LinkEnd.None is not a valid link end.", nameof(end)),
			LinkEnd.Source => Builders<BsonDocument>.Filter.Eq("SourceId", nodeId),
			LinkEnd.Target => Builders<BsonDocument>.Filter.Eq("TargetId", nodeId),
			_ => Builders<BsonDocument>.Filter.Or(
				Builders<BsonDocument>.Filter.Eq("SourceId", nodeId),
				Builders<BsonDocument>.Filter.Eq("TargetId", nodeId)),
		};
		return linksMongo.Find(Builders<BsonDocument>.Filter.And(StreamFilter(), LinkTypeFilter(linkType), endpointFilter)).ToList()
			.Select(LoadLink)
			.ToArray();
	}

	IReadOnlyList<Link> ILinkIndex.LinksBetween(Guid a, Guid b, Type linkType)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var endpointFilter = Builders<BsonDocument>.Filter.Or(
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", a), Builders<BsonDocument>.Filter.Eq("TargetId", b)),
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", b), Builders<BsonDocument>.Filter.Eq("TargetId", a)));
		return linksMongo.Find(Builders<BsonDocument>.Filter.And(StreamFilter(), LinkTypeFilter(linkType), endpointFilter)).ToList()
			.Select(LoadLink)
			.ToArray();
	}

	bool ILinkIndex.TryGetByKey(LinkKey key, out Link? link)
	{
		var linkType = key.LinkType;
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var endpointFilter = Builders<BsonDocument>.Filter.Or(
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", key.X), Builders<BsonDocument>.Filter.Eq("TargetId", key.Y)),
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", key.Y), Builders<BsonDocument>.Filter.Eq("TargetId", key.X)));

		foreach (var doc in linksMongo.Find(Builders<BsonDocument>.Filter.And(StreamFilter(), LinkTypeFilter(linkType), endpointFilter)).ToList())
		{
			// StructuralKey only depends on SourceId/TargetId/type, all already on the raw document,
			// so a bare FromDocument (no Attach) is enough just to evaluate it — LoadLink is reserved
			// for the actual match, which is what callers keep and navigate from.
			var candidate = (Link)FromDocument(doc);
			if (candidate.StructuralKey.Equals(key))
			{
				link = LoadLink(doc);
				return true;
			}
		}
		link = null;
		return false;
	}

	bool ILinkIndex.TryGetById(Guid linkId, out Link? link)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var filter = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", linkId), StreamFilter());
		var doc = linksMongo.Find(filter).FirstOrDefault();
		if (doc is null)
		{
			link = null;
			return false;
		}
		link = LoadLink(doc); // concrete type resolved from the "_t" discriminator
		return true;
	}
}
