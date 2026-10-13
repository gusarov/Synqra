using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
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
/// state as documents in MongoDB — one shared <c>Components</c> collection per database, one document per
/// component keyed by its id (<c>_id</c>); a root component (the entity's own data) is the document whose
/// <c>_id == _eid</c>, a facet is any other document with <c>_eid</c> pointing at its owner.
/// <para>
/// Unlike the in-memory projection (which rebuilds RAM state by replaying the whole log on startup), the
/// materialized read-model is itself durable, so a restarted projection serves current state straight
/// from Mongo with no replay. Documents are stored as the model's JSON shape (round-tripped through the
/// registered <see cref="JsonSerializerOptions"/>), which keeps them queryable and independent of CLR
/// binary layout.
/// </para>
/// <para>
/// <b>Tracking is per-process and lazy.</b> There is no startup replay: an entity is tracked once this
/// instance has created it, enumerated it through <see cref="GetCollection{T}"/>, or resolved it by id
/// through <see cref="ResolveObject"/>, which loads an untracked root from Mongo on demand (so link
/// navigation and facet events work for any entity in the stream, not only ones already touched).
/// <see cref="GetId"/> is the one tracked-only call: it maps a live instance back to its id. Link
/// <i>queries</i> (<see cref="ILinkIndex"/>) always hit Mongo directly, and <i>property changes</i>
/// (<see cref="VisitAsync(ObjectPropertyChangedEvent, EventVisitorContext)"/> /
/// <see cref="VisitAsync(ComponentPropertyChangedEvent, EventVisitorContext)"/>) are field-level
/// <c>$set</c> writes addressed by id + stream (see <see cref="SetField"/>) — a tracked instance is kept in
/// sync when there is one, but neither ever requires the entity to be materialized in RAM.
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
	FilterDefinition<BsonDocument> BuildLinkTypeFilter(Type linkType)
		=> Builders<BsonDocument>.Filter.Eq(DiscriminatorField, linkType.Name);

	/// <summary>Every read/write below is scoped to this instance's <see cref="StreamId"/> — see
	/// the property's own remarks for why.</summary>
	FilterDefinition<BsonDocument> StreamFilter
		=> Builders<BsonDocument>.Filter.Eq(StreamIdField, StreamId);

	readonly IMongoDatabase _database;
	readonly ISbxSerializerFactory _serializerFactory;
	readonly IAppendStorage? _eventStorage;
	readonly JsonSerializerOptions _jsonSerializerOptions;
	readonly IServiceProvider? _serviceProvider;
	readonly ISynqraIdProvider _ids;
	public ISynqraIdProvider IdProvider => _ids;

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
	/// field <see cref="ToDocument"/>/<see cref="CreateComponent"/> stamp on every write. A single
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
		, ISynqraIdProvider? idProvider = null
		)
	{
		_ids = idProvider ?? SynqraIdProvider.Default;
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
		if (!string.IsNullOrEmpty(collectionName))
		{
			// Named collections are retired (docs/model.md §2); the shared Components layout has no column for one.
			throw new NotSupportedException($"Named collections are not supported by the Mongo projection ('{type.Name}', '{collectionName}'). Use the default collection.");
		}
		var collectionId = TypeMetadataProvider.GetTypeMetadata(type).CollectionId;
		lock (_collectionsGate)
		{
			if (_collections.TryGetValue(collectionId, out var existing))
			{
				return existing;
			}
			var gtype = typeof(MongoStoreCollection<>).MakeGenericType(type);
			var created = (MongoStoreCollection)Activator.CreateInstance(gtype, this, StreamId, collectionId, _serializerFactory)!;
			_collections[collectionId] = created;
			return created;
		}
	}

	public Guid GetId(object model)
	{
		if (_byModel.TryGetValue(model, out var tracked))
		{
			return tracked.Id;
		}
		throw new InvalidOperationException("The object is not attached to this MongoProjection.");
	}

	/// <summary>
	/// Resolves an entity by id: the tracked instance if this process already holds it, otherwise its
	/// root component loaded from Mongo (one query on the shared Components collection by <c>_id</c> +
	/// stream) and attached, so the same instance comes back on every later call. Null when no such
	/// root exists in this stream. Roots only — a facet or link id is not an entity.
	/// </summary>
	public object? ResolveObject(Guid id)
	{
		if (id == default)
		{
			return null;
		}
		if (TryGetTracked(id, out var tracked))
		{
			return tracked;
		}
		var doc = Components.Find(Builders<BsonDocument>.Filter.And(BuildRootFilter(id), RootsOnlyFilter)).FirstOrDefault();
		if (doc is null)
		{
			return null;
		}
		var model = FromDocument(doc);
		return AttachWithId(model, id, TypeMetadataProvider.GetTypeMetadata(model.GetType()).CollectionId); // winner-takes-all on a race
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
			cmd.CommandId = _ids.CreateCommandId(SynqraIdProviderExtensions.DeclaredTypeId(cmd.GetType()));
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
		var id = _ids.CreateComponentId();
		AttachWithId(model, id, collectionId);
		return id;
	}

	/// <summary>Tracks <paramref name="model"/> under <paramref name="id"/> and returns the instance that owns
	/// that id — callers must use the returned instance. Winner-takes-all; see docs/model.md §9 "attach race".</summary>
	internal object AttachWithId(object model, Guid id, Guid collectionId)
	{
		if (_byModel.TryGetValue(model, out var existing))
		{
			if (existing.Id != id)
			{
				throw new InvalidOperationException($"Object already attached with a different id ({existing.Id} != {id}).");
			}
			return model;
		}
		// Build the instance completely (bound + facets hydrated) BEFORE publishing it, so no one can
		// observe a half-built winner. A loser is rolled back from _byModel and dropped.
		_byModel.Add(model, new TrackedObject { Id = id, CollectionId = collectionId });
		if (model is IBindableModel bindable && bindable.Store is null)
		{
			bindable.Attach(this, collectionId);
		}
		if (model is IComponentContainer container)
		{
			LoadComponentsInto(container, id, collectionId); // empty for a brand new container
		}
		var winner = _byId.GetOrAdd(id, model);
		if (!ReferenceEquals(winner, model))
		{
			_byModel.Remove(model);
		}
		return winner;
	}

	internal bool TryGetTracked(Guid id, out object model) => _byId.TryGetValue(id, out model!);

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
		clone.Remove(EntityIdField); // projection bookkeeping on every document, root or facet
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

	FilterDefinition<BsonDocument> BuildRootFilter(Guid id)
		=> Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", id), StreamFilter);

	// Create path only: a root document is written whole exactly once (ComponentAddedEvent with
	// ComponentId == TargetId). Every later property change is a field-level SetField.
	// A root is self-owned: _id == _eid == entity id, in the same Components collection as its facets.
	void CreateRoot(Guid id, object model)
	{
		var doc = ToDocument(model, id);
		doc[EntityIdField] = new BsonBinaryData(id, GuidRepresentation.Standard);
		Components.ReplaceOne(BuildRootFilter(id), doc, new ReplaceOptions { IsUpsert = true });
	}

	// One statement drops the entity: its root (_eid == _id) and every facet hanging off _eid. Same
	// observable result as the in-memory projection, where facets live on the removed instance.
	void DeleteEntity(Guid id)
		=> Components.DeleteMany(Builders<BsonDocument>.Filter.And(StreamFilter, Builders<BsonDocument>.Filter.Eq(EntityIdField, id)));

	/// <summary>
	/// Applies one property change as a field-level write: <c>UpdateOne(filter, {$set: {element: value}})</c>.
	/// The element name and the value serializer come from the type's own <see cref="BsonClassMap"/> — the
	/// very class map <see cref="ToDocument"/> serializes through — so the stored field is byte-identical to
	/// what a full re-serialization would have produced, and the write needs no materialized instance at all.
	/// A null value drops the field when the class map says so (the IgnoreIfNull convention), matching
	/// <see cref="ToDocument"/>, which never writes such a field. Never upserts: a change to a document that
	/// does not exist (within this stream) is an inconsistency and throws rather than minting a partial
	/// document out of a single field.
	/// </summary>
	void SetField(IMongoCollection<BsonDocument> collection, FilterDefinition<BsonDocument> filter, Type type, string propertyName, object? value, string what)
	{
		var classMap = BsonClassMap.LookupClassMap(type);
		var memberMap = classMap.GetMemberMap(propertyName);
		if (memberMap is null)
		{
			// A [JsonIgnore] member is unmapped by convention and never persisted — nothing to write.
			var property = type.GetProperty(propertyName)
				?? throw new InvalidOperationException($"'{type.Name}' has no property '{propertyName}'.");
			if (property.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true).Length > 0)
			{
				return;
			}
			throw new InvalidOperationException($"'{type.Name}.{propertyName}' is not mapped to a document field.");
		}

		UpdateDefinition<BsonDocument> update;
		if (value is null && memberMap.IgnoreIfNull)
		{
			update = Builders<BsonDocument>.Update.Unset(memberMap.ElementName);
		}
		else
		{
			update = Builders<BsonDocument>.Update.Set(memberMap.ElementName, SerializeMember(memberMap, CoerceValue(memberMap, value)));
		}

		var result = collection.UpdateOne(filter, update, new UpdateOptions { IsUpsert = false });
		if (result.MatchedCount == 0)
		{
			throw new InvalidOperationException($"Cannot apply a property change to {what}: no document matched in stream {StreamId}.");
		}
	}

	// Same conversion ComponentApplyHelpers.ApplyPropertyChange does on the live instance (an event
	// rehydrated from the log can carry e.g. a long for a double member), so an untracked write
	// serializes the identical CLR value a tracked one would.
	object? CoerceValue(BsonMemberMap memberMap, object? value)
	{
		var memberType = Nullable.GetUnderlyingType(memberMap.MemberType) ?? memberMap.MemberType;
		if (value is IConvertible convertible && value.GetType() != memberType && memberType != typeof(object))
		{
			return convertible.ToType(memberType, CultureInfo.InvariantCulture);
		}
		return value;
	}

	// Serializes through the member's own serializer (the one ToDocument would use for this element)
	// into a throwaway wrapper document and lifts the resulting BsonValue out.
	BsonValue SerializeMember(BsonMemberMap memberMap, object? value)
	{
		var wrapper = new BsonDocument();
		using (var writer = new BsonDocumentWriter(wrapper))
		{
			var context = BsonSerializationContext.CreateRoot(writer);
			writer.WriteStartDocument();
			writer.WriteName("v");
			memberMap.GetSerializer().Serialize(context, value);
			writer.WriteEndDocument();
		}
		return wrapper["v"];
	}

	// ---------------------------------------------------------------- ICommandVisitor

	public Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;

	public Task VisitAsync(ChangeObjectPropertyCommand cmd, CommandHandlerContext ctx)
	{
		ctx.Events.Add(new ObjectPropertyChangedEvent
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
		});
		return Task.CompletedTask;
	}

	public Task VisitAsync(DeleteObjectCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;

	public Task VisitAsync(AddComponentCommand cmd, CommandHandlerContext ctx)
	{
		// Uniqueness / veto checks happen during event apply (where the live ComponentsCollection
		// lives) — same pattern as ChangeObjectProperty.
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

	public Task VisitAsync(AddLinkCommand cmd, CommandHandlerContext ctx)
	{
		// Structural dedup happens during event apply (queried straight from Mongo, since link
		// queries always hit Mongo directly — see the type-level remarks) — same pattern as
		// AddComponentCommand's uniqueness check.
		ctx.Events.Add(new LinkAddedEvent
		{
			StreamId = cmd.StreamId,
			CommandId = cmd.CommandId,
			EventId = _ids.CreateEventId<LinkAddedEvent>(cmd.CommandId, 1),
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
			EventId = _ids.CreateEventId<LinkRemovedEvent>(cmd.CommandId, 1),
			LinkId = cmd.LinkId,
		});
		return Task.CompletedTask;
	}

	// ---------------------------------------------------------------- IEventVisitor

	public Task BeforeVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;

	public Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx)
	{
		// The live instance is kept in sync when this process holds it; the durable write never needs it.
		if (TryGetTracked(ev.TargetId, out var model))
		{
			ComponentApplyHelpers.ApplyPropertyChange(model, ev.PropertyName, ev.NewValue);
		}
		var type = TypeMetadataProvider.GetTypeMetadata(ev.TargetTypeId).Type;
		SetField(Components, BuildRootFilter(ev.TargetId), type, ev.PropertyName, ev.NewValue, $"entity {ev.TargetId}");
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
			// Track + persist it as a self-owned document in the shared Components collection.
			var rootType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
			object rootModel = ev.Data ?? (TryGetTracked(ev.TargetId, out var tracked) ? tracked : Activator.CreateInstance(rootType)!);
			// Race: another caller may materialize this same id concurrently — the collection enumerator or
			// ResolveObject on a re-delivered add (the document already exists then). AttachWithId registers
			// id -> instance atomically and hands back whichever instance won; using that one for the write
			// keeps the tracked instance and the stored document the same object. On a first add nothing can
			// race (no document to load yet), so the winner is ev.Data itself.
			rootModel = AttachWithId(rootModel, ev.TargetId, ev.CollectionId);
			CreateRoot(ev.TargetId, rootModel);
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

		CreateComponent(ev.TargetId, ev.ComponentTypeId, ev.ComponentId, component);
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	public Task VisitAsync(ComponentPropertyChangedEvent ev, EventVisitorContext ctx)
	{
		// The live instance (root entity, or the facet on its tracked container) is kept in sync when
		// this process holds it; the durable write below never needs it. A tracked container that
		// lacks the facet is still an inconsistency and throws (ResolveTarget).
		if (TryGetTracked(ev.TargetId, out var trackedModel))
		{
			var target = ComponentApplyHelpers.ResolveTarget(trackedModel, ev, TypeMetadataProvider);
			ComponentApplyHelpers.ApplyPropertyChange(target, ev.PropertyName, ev.NewValue);
		}

		// Durable persistence still forks: a root component is its entity's own document, a facet
		// lives in the shared Components collection. Either way it is a single-field update.
		var componentType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		if (ev.ComponentId == ev.TargetId)
		{
			SetField(Components, BuildRootFilter(ev.TargetId), componentType, ev.PropertyName, ev.NewValue, $"root component {ev.TargetId}");
		}
		else
		{
			SetField(Components, BuildComponentFilter(ev.TargetId, componentType, ev.ComponentId), componentType, ev.PropertyName, ev.NewValue, $"component {ev.ComponentId} of {ev.TargetId}");
		}
		MarkApplied(ev.TargetId, ev.EventId);
		return Task.CompletedTask;
	}

	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx)
	{
		if (ev.ComponentId == ev.TargetId)
		{
			// Root delete: the entity and its facets go in one DeleteMany by _eid; incident links are
			// not cascaded yet (they live in Links, keyed by Source/Target).
			DeleteEntity(ev.TargetId);
			if (_byId.TryRemove(ev.TargetId, out var rootModel))
			{
				_byModel.Remove(rootModel);
			}
			MarkApplied(ev.TargetId, ev.EventId);
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
		link.LinkId = ev.LinkId == default ? _ids.CreateLinkId() : ev.LinkId;
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
		var replaceFilter = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", link.LinkId), StreamFilter);
		linksMongo.ReplaceOne(replaceFilter, doc, new ReplaceOptions { IsUpsert = true });

		if (link is IBindableModel bindable && bindable.Store is null)
		{
			bindable.Attach(this, TypeMetadataProvider.GetTypeMetadata(linkType).CollectionId);
		}
		return Task.CompletedTask;
	}

	public Task VisitAsync(LinkRemovedEvent ev, EventVisitorContext ctx)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var deleteFilter = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", ev.LinkId), StreamFilter);
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
		var typeIdFilter = BuildLinkTypeFilter(link.GetType());
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

		foreach (var doc in linksMongo.Find(Builders<BsonDocument>.Filter.And(StreamFilter, typeIdFilter, endpointFilter)).ToList())
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

	// ResolveObject, not TryGetTracked: a facet add/delete needs the live container (uniqueness / veto
	// run against its Components collection), so an untracked container is loaded on demand.
	IComponentContainer ResolveContainer(Guid targetId)
		=> ComponentApplyHelpers.ResolveContainer(ResolveObject(targetId), targetId);

	const string ComponentsMongoCollectionName = "Components";

	/// <summary>The one shared collection every root and facet lives in. Its indexes are created by
	/// <c>ComponentsIndexesUpgrade</c> at host startup, like every other storage upgrade.</summary>
	internal IMongoCollection<BsonDocument> Components => _database.GetCollection<BsonDocument>(ComponentsMongoCollectionName);

	// A root owns itself: _id == _eid (docs/model.md §2). Only applied on single-document lookups (ResolveObject),
	// where the _id index already narrows to one document. Type-scoped enumeration needs no marker: "_t" is
	// the concrete type, and a type is either a root type or a facet type.
	internal FilterDefinition<BsonDocument> RootsOnlyFilter => new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$_id", "$" + EntityIdField }));

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
	/// links are stored. A facet's <c>_id</c> IS its component id (docs/model.md §2: every component has a
	/// first-class <c>_id</c> in the unified id space), so addressing is <c>_id</c> + stream;
	/// <paramref name="containerId"/> (<see cref="EntityIdField"/>) and <paramref name="componentType"/>
	/// (<c>_t</c>) stay in the filter as a consistency guard.
	/// </summary>
	FilterDefinition<BsonDocument> BuildComponentFilter(Guid containerId, Type componentType, Guid componentId) =>
		Builders<BsonDocument>.Filter.And(
			StreamFilter,
			Builders<BsonDocument>.Filter.Eq("_id", componentId),
			Builders<BsonDocument>.Filter.Eq(EntityIdField, containerId),
			Builders<BsonDocument>.Filter.Eq(DiscriminatorField, componentType.Name));

	// Create path only (facet add); a later property change is a field-level SetField on this document.
	void CreateComponent(Guid containerId, Guid componentTypeId, Guid componentId, IComponent component)
	{
		// Native driver serialization through nominal type object -> always writes "_t" (see ToDocument).
		// Explicit serializer, not ambient lookup — see ToDocument's own remarks.
		if (componentId == Guid.Empty)
		{
			throw new InvalidOperationException($"A '{component.GetType().Name}' facet on {containerId} has no component id; every component is persisted under its own _id.");
		}
		var doc = component.ToBsonDocument(typeof(object), MongoEventClassMaps.ScopedOpenObjectSerializer);
		doc["_id"] = new BsonBinaryData(componentId, GuidRepresentation.Standard);
		doc[EntityIdField] = new BsonBinaryData(containerId, GuidRepresentation.Standard);
		doc[StreamIdField] = new BsonBinaryData(StreamId, GuidRepresentation.Standard);
		Components.ReplaceOne(BuildComponentFilter(containerId, component.GetType(), componentId), doc, new ReplaceOptions { IsUpsert = true });
	}

	void DeleteComponentDoc(Guid containerId, Guid componentTypeId, Guid componentId)
		=> Components.DeleteOne(BuildComponentFilter(containerId, TypeMetadataProvider.GetTypeMetadata(componentTypeId).Type, componentId));

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
		// Exclude the container itself: its root document also has _eid == _id but is not one of its facets.
		var loadFilter = Builders<BsonDocument>.Filter.And(StreamFilter, Builders<BsonDocument>.Filter.Eq(EntityIdField, containerId), Builders<BsonDocument>.Filter.Ne("_id", containerId));
		foreach (var doc in Components.Find(loadFilter).ToList())
		{
			// Concrete type comes from the "_t" discriminator inside the document (see CreateComponent).
			var component = (IComponent)FromDocument(doc);
			// The component id is generated hidden state, not a serialized member — restore it from _id.
			if (component is IBindableComponent identified)
			{
				identified.SetComponentId(doc["_id"].AsGuid);
			}

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
			bindable.Attach(this, TypeMetadataProvider.GetTypeMetadata(link.GetType()).CollectionId);
		}
		return link;
	}

	IReadOnlyCollection<Link> ILinkIndex.Links
	{
		get
		{
			var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
			return linksMongo.Find(StreamFilter).ToList()
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
		return linksMongo.Find(Builders<BsonDocument>.Filter.And(StreamFilter, BuildLinkTypeFilter(linkType), endpointFilter)).ToList()
			.Select(LoadLink)
			.ToArray();
	}

	IReadOnlyList<Link> ILinkIndex.LinksBetween(Guid a, Guid b, Type linkType)
	{
		var linksMongo = _database.GetCollection<BsonDocument>(LinksMongoCollectionName);
		var endpointFilter = Builders<BsonDocument>.Filter.Or(
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", a), Builders<BsonDocument>.Filter.Eq("TargetId", b)),
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", b), Builders<BsonDocument>.Filter.Eq("TargetId", a)));
		return linksMongo.Find(Builders<BsonDocument>.Filter.And(StreamFilter, BuildLinkTypeFilter(linkType), endpointFilter)).ToList()
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

		foreach (var doc in linksMongo.Find(Builders<BsonDocument>.Filter.And(StreamFilter, BuildLinkTypeFilter(linkType), endpointFilter)).ToList())
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
		var filter = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", linkId), StreamFilter);
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
