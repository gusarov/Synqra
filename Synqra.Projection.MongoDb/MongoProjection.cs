using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Synqra.AppendStorage;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;

namespace Synqra.Projection.MongoDb;

using IAppendStorage = IEventLog;

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
public sealed class MongoProjection : IObjectStore, IProjection, ILinkIndex, IComponentIndex, ICommittedEventSource
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

	// Links are components (docs/model.md §4): they live in the shared Components collection, owned by
	// their source (_eid == SourceId), told apart by the same "_t" discriminator every document carries —
	// the link type's simple name, so a per-type query filters on it directly.
	const string DiscriminatorField = "_t";
	// Short, underscore-prefixed system column — matches the "_id"/"_t" naming convention. NOT
	// "_cid" ("ContainerId", the historical Synqra name for this concept — see
	// SynqraGuids.SynqraRootStreamId's own remark): "container" is being phased out of the
	// vocabulary entirely now that Components (see EntityIdField below) also legitimately use that
	// word for something unrelated (IComponentContainer — the object a component is attached to).
	// Stream and node are the two concepts that actually exist; "container" isn't a third one.
	const string StreamIdField = "_sid";
	/// <summary>The last event applied to an entity or any of its facets, on the root document, so a version survives a restart and is visible to every process.</summary>
	const string LastEventIdField = "_lev";

	/// <summary>
	/// A link type matches every registered concrete link type assignable to it (itself included), as
	/// the in-memory index's IsInstanceOfType does. Computed per query: the registry is additive.
	/// </summary>
	FilterDefinition<BsonDocument> BuildLinkTypeFilter(Type linkType)
		=> Builders<BsonDocument>.Filter.In(DiscriminatorField, ConcreteTypeNamesOf(linkType));

	IEnumerable<string> ConcreteTypeNamesOf(Type baseType) => TypeMetadataProvider.RegisteredTypes
		.Select(m => m.Type)
		.Where(t => !t.IsAbstract && baseType.IsAssignableFrom(t))
		.Select(t => t.Name)
		.Distinct();

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
	// GetId() resolves a live instance to its key. Weak both ways: the projection never keeps an entity
	// alive; one nobody holds is collected and loaded again from Mongo on demand.
	readonly ConditionalWeakTable<object, TrackedObject> _byModel = new();
	// Weak on purpose: holding the TrackedObject here would keep it reachable, so its finalizer would never drop this slot.
	// Keyed by stream as well: one multitenant instance serves every stream, and an id seen in one must never answer in another.
	readonly ConcurrentDictionary<(Guid Stream, Guid Id), WeakReference<object>> _byId = new();
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
		  [FromKeyedServices] IMongoDatabase database
		, ISbxSerializerFactory serializerFactory
		, ITypeMetadataProvider typeMetadataProvider
		, [FromKeyedServices] IEventLog? eventStorage = null
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

	/// <summary>
	/// What the projection knows about one tracked instance. It lives in the ConditionalWeakTable keyed by
	/// that instance, so it becomes unreachable exactly when the instance is collected; its finalizer then
	/// drops the instance's id slot, and only that slot (a newer instance re-attached under the same id
	/// holds a different WeakReference and stays).
	/// </summary>
	sealed class TrackedObject
	{
		/// <summary>The collection the instance belongs to: its plane (CollectionId), its root type (Type) and its projection; one reference instead of copies of each.</summary>
		public required MongoStoreCollection Collection { get; init; }
		public required Guid Id { get; init; }
		/// <summary>The stream the instance was attached in; it is tracked, and answers lookups, only there.</summary>
		public required Guid StreamId { get; init; }
		public required WeakReference<object> Slot { get; init; }
		public Guid LastEventId { get; set; }

		~TrackedObject()
		{
			Collection.Projection._byId.TryRemove(new KeyValuePair<(Guid, Guid), WeakReference<object>>((StreamId, Id), Slot));
		}
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
		var doc = FindDocument(Components, Builders<BsonDocument>.Filter.And(BuildRootFilter(id), RootsOnlyFilter));
		return doc is null ? null : AttachLoaded(doc, id, collection: null);
	}

	/// <summary>Tracks an entity read from its root document, restoring its persisted version. Winner-takes-all on a race.</summary>
	/// <param name="collection">The collection the document was read through; by id alone (no plane stored per document yet) the instance type's default collection.</param>
	/// <param name="facets">Its facet documents when the caller read them with others; otherwise they are read here.</param>
	internal object AttachLoaded(BsonDocument doc, Guid id, MongoStoreCollection? collection, IEnumerable<BsonDocument>? facets = null)
	{
		var model = FromDocument(doc);
		var winner = AttachWithId(model, id, collection ?? GetCollection(model.GetType(), ""), facets);
		if (doc.TryGetValue(LastEventIdField, out var lev) && lev is BsonBinaryData binary && _byModel.TryGetValue(winner, out var tracked) && tracked.LastEventId == default)
		{
			tracked.LastEventId = binary.ToGuid(GuidRepresentation.Standard);
		}
		return winner;
	}

	public Guid GetLastEventId(Guid targetId)
		=> TryGetTracked(targetId, out var model) && _byModel.TryGetValue(model, out var tracked)
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
		ResolveTargetIds(cmd);
		return SubmitSerializedAsync(cmd, options);
	}

	/// <summary>Fills a command's target ids from its live TargetObject when the caller gave the instance, not the ids.</summary>
	void ResolveTargetIds(Command cmd)
	{
		if (cmd is SingleObjectCommand soc && soc.TargetObject is not null)
		{
			var tracked = _byModel.GetValue(soc.TargetObject, _ => throw new InvalidOperationException("Target object is not attached."));
			if (soc.TargetId == default)
			{
				soc.TargetId = tracked.Id;
			}
			if (soc.CollectionId == default)
			{
				soc.CollectionId = tracked.Collection.CollectionId;
			}
			var typeId = TypeMetadataProvider.GetTypeMetadata(soc.TargetObject.GetType()).TypeId;
			if (soc.TargetTypeId == default)
			{
				soc.TargetTypeId = typeId;
			}
		}
	}

	// One write at a time per stream (this projection serves every stream); writers in other processes
	// are serialized by the transaction itself (a conflict is retried by the driver until it lands).
	readonly ConcurrentDictionary<Guid, ReentrantGate> _streamGates = new();

	ReentrantGate GateOf(Guid streamId) => _streamGates.GetOrAdd(streamId, _ => new ReentrantGate());

	// The write in flight: its session, the documents it works on, the instance updates to run after commit,
	// and the entities it attached (evicted on abort: tracking is a cache of committed state).
	// A write reads the documents of the entities it touches once, into Documents, decides every check
	// there, keeps them as its events leave them, and sends what it did as one bulk write of its net
	// effect: a batch costs a few round trips, not a few per operation.
	sealed class WriteScope(IClientSessionHandle session)
	{
		public IClientSessionHandle Session { get; } = session;
		public List<Action> AfterCommit { get; } = [];
		public List<Guid> Attached { get; } = [];
		/// <summary>Ids of the facets and links this write deletes: gone from the stored state already, still on the live container until the commit.</summary>
		public HashSet<Guid> Removed { get; } = [];
		/// <summary>By <c>_id</c>: the stored documents of the <see cref="Loaded"/> entities and the ones this write made, as this write leaves them.</summary>
		public Dictionary<Guid, BsonDocument> Documents { get; } = [];
		/// <summary>The entities whose own documents (root, facets, links) and the links pointing at them are all in <see cref="Documents"/>.</summary>
		public HashSet<Guid> Loaded { get; } = [];
		/// <summary>Not sent yet: the documents written whole from <see cref="Documents"/>.</summary>
		public HashSet<Guid> Changed { get; } = [];
		/// <summary>Not sent yet: the documents deleted.</summary>
		public HashSet<Guid> Dropped { get; } = [];
		/// <summary>Not sent yet: the entities deleted with everything they own.</summary>
		public HashSet<Guid> DroppedEntities { get; } = [];
		/// <summary>Not sent yet: field updates of documents the write never read (a version stamp on the owner of a link it deleted).</summary>
		public List<WriteModel<BsonDocument>> Updates { get; } = [];

		public bool IsPending => Changed.Count > 0 || Dropped.Count > 0 || DroppedEntities.Count > 0 || Updates.Count > 0;
	}

	// Per async flow, so a reader or an inbound event on another flow never sees this write's session.
	readonly AsyncLocal<WriteScope?> _scope = new();

	WriteScope Scope => _scope.Value ?? throw new InvalidOperationException("No write in flight; every apply runs inside ApplyInWriteAsync.");

	/// <summary>The session of the write in flight, its pending writes sent first, so a read through it sees them; null outside a write.</summary>
	IClientSessionHandle? FlushSession()
	{
		var scope = _scope.Value;
		if (scope is not null)
		{
			Flush(scope);
		}
		return scope?.Session;
	}

	/// <summary>
	/// Sends what the write did so far as one ordered bulk write of its net effect: the deletes first (an entity with
	/// everything it owns, by <c>_eid</c>; a document by <c>_id</c>), then every document it made or changed, whole,
	/// then the field updates of documents it never read. Mongo runs it as one delete and one update command.
	/// </summary>
	void Flush(WriteScope scope)
	{
		if (scope.IsPending)
		{
			var filters = Builders<BsonDocument>.Filter;
			var writes = new List<WriteModel<BsonDocument>>();
			writes.AddRange(scope.DroppedEntities.Select(id => new DeleteManyModel<BsonDocument>(filters.And(StreamFilter, filters.Eq(EntityIdField, id)))));
			writes.AddRange(scope.Dropped.Select(id => new DeleteOneModel<BsonDocument>(filters.And(StreamFilter, filters.Eq("_id", id)))));
			writes.AddRange(scope.Changed.Select(id => new ReplaceOneModel<BsonDocument>(BuildDocumentFilter(scope.Documents[id]), scope.Documents[id]) { IsUpsert = true, }));
			writes.AddRange(scope.Updates);
			Components.BulkWrite(scope.Session, writes, new BulkWriteOptions { IsOrdered = true, });
			scope.DroppedEntities.Clear();
			scope.Dropped.Clear();
			scope.Changed.Clear();
			scope.Updates.Clear();
		}
	}

	/// <summary>
	/// What a whole-document write replaces: a root by <c>_id</c>, a facet or link by <c>_id</c>, owner and type, so a
	/// stored document of another owner or type under the same id is never overwritten (the upsert fails on <c>_id</c>).
	/// </summary>
	FilterDefinition<BsonDocument> BuildDocumentFilter(BsonDocument doc)
	{
		var id = doc["_id"].AsGuid;
		var owner = doc[EntityIdField].AsGuid;
		return id == owner
			? BuildRootFilter(id)
			: Builders<BsonDocument>.Filter.And(StreamFilter, Builders<BsonDocument>.Filter.Eq("_id", id), Builders<BsonDocument>.Filter.Eq(EntityIdField, owner), Builders<BsonDocument>.Filter.Eq(DiscriminatorField, doc[DiscriminatorField].AsString));
	}

	/// <summary>
	/// Reads the stored documents of these entities into the write, once each: their own (root, facets, links)
	/// and the links pointing at them, all in one query by the <c>(_sid, _eid)</c> and <c>(_sid, TargetId)</c> indexes.
	/// </summary>
	void Load(IEnumerable<Guid> entityIds)
	{
		var scope = Scope;
		var missing = entityIds.Where(id => id != default && !scope.Loaded.Contains(id)).Distinct().ToList();
		if (missing.Count > 0)
		{
			Flush(scope); // the stored state must hold what this write did before it is read again
			// The stream in each branch, not around the $or: each branch then scans its own index within its own bounds.
			var filters = Builders<BsonDocument>.Filter;
			var filter = filters.Or(
				  filters.And(StreamFilter, filters.In(EntityIdField, missing))
				, filters.And(StreamFilter, filters.Exists(nameof(Link.TargetId)), filters.In(nameof(Link.TargetId), missing))
			);
			foreach (var doc in Components.Find(scope.Session, filter, new FindOptions { BatchSize = WholeReadBatchSize, }).ToList())
			{
				scope.Documents[doc["_id"].AsGuid] = doc;
			}
			scope.Loaded.UnionWith(missing);
		}
	}

	/// <summary>A read the caller needs whole comes back in one reply (the server still caps a reply at 16 MB), not 101 documents and then more.</summary>
	const int WholeReadBatchSize = 100_000;

	/// <summary>The entity's root document in the write, or null when the stream has none.</summary>
	BsonDocument? FindRoot(Guid entityId)
	{
		Load([entityId]);
		return Scope.Documents.TryGetValue(entityId, out var doc) && doc[EntityIdField].AsGuid == entityId ? doc : null;
	}

	/// <summary>The entity's facet or link document with this id and type, in the write; what <see cref="BuildComponentFilter"/> matches.</summary>
	BsonDocument? FindOwned(Guid entityId, Type componentType, Guid componentId)
	{
		Load([entityId]);
		return Scope.Documents.TryGetValue(componentId, out var doc) && doc[EntityIdField].AsGuid == entityId && doc[DiscriminatorField].AsString == componentType.Name ? doc : null;
	}

	/// <summary>The entity's facets and links in the write, its root excluded.</summary>
	IEnumerable<BsonDocument> FindFacets(Guid entityId)
	{
		Load([entityId]);
		return Scope.Documents.Values.Where(doc => doc[EntityIdField].AsGuid == entityId && doc["_id"].AsGuid != entityId).ToList();
	}

	Guid? ReadGuid(BsonDocument doc, string field) => doc.TryGetValue(field, out var value) && value is BsonBinaryData ? value.AsGuid : null;

	/// <summary>Writes a whole document into the write.</summary>
	void Upsert(BsonDocument doc)
	{
		var scope = Scope;
		var id = doc["_id"].AsGuid;
		scope.Documents[id] = doc;
		scope.Dropped.Remove(id);
		scope.Changed.Add(id);
	}

	/// <summary>Marks a document of the write as changed in place.</summary>
	void Touch(BsonDocument doc) => Scope.Changed.Add(doc["_id"].AsGuid);

	/// <summary>Deletes a document from the write.</summary>
	void Delete(Guid id)
	{
		var scope = Scope;
		scope.Documents.Remove(id);
		scope.Changed.Remove(id);
		scope.Dropped.Add(id);
	}

	Task SubmitSerializedAsync(Command cmd, CommandSubmissionOptions? options)
		=> GateOf(cmd.StreamId).RunAsync(() => ProcessCommandAsync(cmd, options));

	/// <summary>
	/// A submitted command is one write. Inside the projection's transaction: the precondition check and
	/// the events' document writes (the validation), then the event-log append on the log's own store,
	/// then the projection commits. The log may live on another cluster, so it cannot join this
	/// transaction; appending before the commit means the only possible gap is a projection behind its
	/// log (a failed final commit), never ahead of it. A retried attempt appends the same events again,
	/// which the log ignores (same ids). A batch is the same path with more events.
	/// </summary>
	async Task ProcessCommandAsync(Command cmd, CommandSubmissionOptions? options)
	{
		var committed = await RunWriteAsync(async scope =>
		{
			Load(TargetsOf(cmd)); // one read for every entity the command names
			if (options is not null && options.ExpectedLastEventId != Guid.Empty && cmd is SingleObjectCommand precheck && precheck.TargetId != default)
			{
				RequireVersion(precheck.TargetId, options.ExpectedLastEventId);
			}
			var ctx = new CommandHandlerContext { EmitCommandEvents = EmitCommandEvents };
			await cmd.AcceptAsync(this, ctx);
			foreach (var ev in ctx.Events)
			{
				await ev.AcceptAsync(this, null);
			}
			Flush(scope); // the document writes are the validation: they land before the log hears of the events
			if (_eventStorage is not null && ctx.Events.Count > 0)
			{
				await _eventStorage.AppendBatchAsync(ctx.Events);
			}
			return ctx.Events;
		});
		if (committed.Count > 0)
		{
			EventsCommitted?.Invoke(committed);
		}
	}

	/// <summary>
	/// One write: a session and a transaction around <paramref name="body"/> (the driver retries it on a
	/// transient conflict). Tracked instances change only after commit, so an in-process reader sees the
	/// write whole or not at all; on abort the entities the write attached are evicted.
	/// </summary>
	async Task<T> RunWriteAsync<T>(Func<WriteScope, Task<T>> body)
	{
		WriteScope? scope = null;
		var committed = false;
		T result;
		try
		{
			result = await _database.Client.RunInTransactionAsync(async (session, _) =>
			{
				scope ??= new WriteScope(session); // a retried attempt reuses the session
				ResetWrite(scope); // and starts from nothing
				_scope.Value = scope; // visible to this attempt's flow only
				var outcome = await body(scope);
				Flush(scope);
				return outcome;
			});
			committed = true;
		}
		finally
		{
			if (!committed && scope is not null)
			{
				ResetWrite(scope);
			}
		}
		foreach (var apply in scope!.AfterCommit)
		{
			apply();
		}
		return result;
	}

	void ResetWrite(WriteScope scope)
	{
		foreach (var id in scope.Attached)
		{
			Evict(id);
		}
		scope.Attached.Clear();
		scope.Removed.Clear();
		scope.AfterCommit.Clear();
		scope.Documents.Clear();
		scope.Loaded.Clear();
		scope.Changed.Clear();
		scope.Dropped.Clear();
		scope.DroppedEntities.Clear();
		scope.Updates.Clear();
	}

	/// <summary>The entities a command writes to, and the ones a batch expects at a version.</summary>
	IEnumerable<Guid> TargetsOf(Command cmd) => cmd switch
	{
		BatchCommand batch => batch.Commands.SelectMany(TargetsOf).Concat(batch.Expectations.Select(e => e.TargetId)),
		SingleObjectCommand single => [single.TargetId,],
		_ => [],
	};

	/// <summary>
	/// An event's writes: inside the write in flight, or, for an event applied from outside a submit
	/// (one arriving from a replication peer), in a write of its own under the same gate.
	/// </summary>
	async Task ApplyInWriteAsync(Action apply)
	{
		if (_scope.Value is not null)
		{
			apply();
		}
		else
		{
			await GateOf(StreamId).RunAsync(() => RunWriteAsync(_ =>
			{
				apply();
				return Task.FromResult(0); // nothing to return
			}));
		}
	}

	public event Action<IEnumerable<Event>>? EventsCommitted;

	/// <summary>Whether commands emit their <see cref="CommandCreatedEvent"/>; see <see cref="CommandHandlerContext.EmitCommandEvents"/>.</summary>
	public bool EmitCommandEvents { get; set; } = true;

	void Evict(Guid id)
	{
		if (_byId.TryRemove((StreamId, id), out var entry) && entry.TryGetTarget(out var model))
		{
			_byModel.Remove(model);
		}
	}

	/// <summary>The entity's version as stored, read inside the transaction so another process's write is seen.</summary>
	Guid ReadVersion(Guid id)
	{
		var doc = FindRoot(id);
		return doc is not null && doc.TryGetValue(LastEventIdField, out var lev) && lev is BsonBinaryData binary ? binary.ToGuid(GuidRepresentation.Standard) : Guid.Empty;
	}

	void RequireVersion(Guid id, Guid expected)
	{
		var actual = ReadVersion(id);
		if (actual != expected)
		{
			throw new ConcurrencyException(id, expected, actual);
		}
	}

	// ---------------------------------------------------------------- driver calls (through the session in flight)

	internal List<BsonDocument> FindDocuments(IMongoCollection<BsonDocument> collection, FilterDefinition<BsonDocument> filter, ProjectionDefinition<BsonDocument>? projection = null)
	{
		var options = new FindOptions { BatchSize = WholeReadBatchSize, };
		var find = FlushSession() is { } session ? collection.Find(session, filter, options) : collection.Find(filter, options);
		return (projection is null ? find : find.Project(projection)).ToList();
	}

	BsonDocument? FindDocument(IMongoCollection<BsonDocument> collection, FilterDefinition<BsonDocument> filter, ProjectionDefinition<BsonDocument>? projection = null)
	{
		var find = FlushSession() is { } session ? collection.Find(session, filter) : collection.Find(filter);
		return (projection is null ? find : find.Project(projection)).FirstOrDefault();
	}

	/// <summary>Runs after the transaction commits: the only moment a tracked instance changes.</summary>
	void RunAfterCommit(Action apply)
	{
		var scope = _scope.Value ?? throw new InvalidOperationException("An event is being applied outside a write; every apply runs inside ApplyInWriteAsync.");
		scope.AfterCommit.Add(apply);
	}

	/// <summary>Whether a <paramref name="componentType"/> may join the live container, counting what this write has already deleted from it as gone.</summary>
	bool CanAddToLive(IComponentContainer container, Type componentType)
	{
		var removed = _scope.Value!.Removed;
		var pending = container.Components.Where(c => c is IIdentifiable<Guid> identified && removed.Contains(identified.Id)).ToList();
		return container.Components.CanAddComponent(componentType, pending);
	}

	// ---------------------------------------------------------------- tracking (internal)

	/// <summary>The open collection with <paramref name="collectionId"/>, or the type's default one; an unknown named collection is refused (the Mongo projection has none).</summary>
	MongoStoreCollection OpenCollection(Guid collectionId, Guid typeId)
	{
		MongoStoreCollection? opened;
		lock (_collectionsGate)
		{
			_collections.TryGetValue(collectionId, out opened);
		}
		var collection = opened ?? GetCollection(TypeMetadataProvider.GetTypeMetadata(typeId).Type, "");
		if (collectionId != default && collection.CollectionId != collectionId)
		{
			throw new InvalidOperationException($"Collection {collectionId} is neither open nor the default collection of type {typeId}; the Mongo projection has no named collections.");
		}
		return collection;
	}

	internal Guid Attach(object model, MongoStoreCollection collection)
	{
		var id = _ids.CreateComponentId();
		AttachWithId(model, id, collection);
		return id;
	}

	/// <summary>Tracks <paramref name="model"/> under <paramref name="id"/> and returns the instance that owns
	/// that id — callers must use the returned instance. Winner-takes-all; see docs/model.md §9 "attach race".</summary>
	/// <param name="facets">The container's facet documents when the caller already read them; otherwise they are read here.</param>
	internal object AttachWithId(object model, Guid id, MongoStoreCollection collection, IEnumerable<BsonDocument>? facets = null)
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
		var entry = new WeakReference<object>(model);
		var key = (Stream: StreamId, Id: id);
		_byModel.Add(model, new TrackedObject { Collection = collection, Id = id, StreamId = key.Stream, Slot = entry });
		if (model is IBindableModel bindable && bindable.Store is null)
		{
			bindable.Attach(this, collection.CollectionId);
		}
		if (model is IComponentContainer container)
		{
			LoadComponentsInto(container, id, collection.CollectionId, facets ?? ReadFacets(id)); // empty for a brand new container
		}
		object? winner = null;
		while (winner is null)
		{
			var current = _byId.GetOrAdd(key, entry);
			if (ReferenceEquals(current, entry))
			{
				winner = model;
			}
			else if (current.TryGetTarget(out var alive))
			{
				winner = alive;
			}
			else if (_byId.TryUpdate(key, entry, current))
			{
				winner = model; // the previous holder of this id was collected
			}
		}
		if (!ReferenceEquals(winner, model))
		{
			_byModel.Remove(model);
		}
		return winner;
	}

	/// <summary>The live instance tracked under <paramref name="id"/> in the current stream; a collected one (slot not yet dropped by its finalizer) counts as untracked.</summary>
	internal bool TryGetTracked(Guid id, out object model)
	{
		model = null!;
		return _byId.TryGetValue((StreamId, id), out var entry) && entry.TryGetTarget(out model!);
	}

	/// <summary>Whether an id slot exists at all in the current stream, live or awaiting its finalizer; for tests.</summary>
	internal bool IsSlotHeld(Guid id) => _byId.ContainsKey((StreamId, id));

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
	/// own class map has no member for it). <c>_id</c> is dropped too: a component's id (a link's included)
	/// is generated hidden state the caller restores from it.
	/// </summary>
	internal object FromDocument(BsonDocument doc, params string[] addressingFields)
	{
		var discriminator = doc["_t"].AsString;
		var type = SynqraJsonTypeInfoResolver.GetByDiscriminator(discriminator)
			?? throw new InvalidOperationException($"Unknown discriminator '{discriminator}' — no model is registered under that name.");

		var clone = (BsonDocument)doc.DeepClone();
		clone.Remove("_id");
		clone.Remove("_t");
		clone.Remove(StreamIdField);
		clone.Remove(EntityIdField); // projection bookkeeping on every document, root or facet
		clone.Remove(LastEventIdField);
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
	/// A type whose own class map already designates an id member gets <c>_id</c> written natively as
	/// part of that serialization, so it's left alone here; a plain model with no such member gets
	/// <paramref name="id"/> written as the document's <c>_id</c>, as a native Guid (not a
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
	void CreateRoot(Guid id, object model, Guid eventId)
	{
		var doc = ToDocument(model, id);
		doc[EntityIdField] = new BsonBinaryData(id, GuidRepresentation.Standard);
		doc[LastEventIdField] = new BsonBinaryData(eventId, GuidRepresentation.Standard);
		Upsert(doc);
	}

	/// <summary>Stamps the entity's version after a facet or link write, which does not touch the root document.</summary>
	void StampVersion(Guid id, Guid eventId)
	{
		var scope = Scope;
		var version = new BsonBinaryData(eventId, GuidRepresentation.Standard);
		if (scope.Documents.TryGetValue(id, out var root))
		{
			root[LastEventIdField] = version;
			Touch(root);
		}
		else
		{
			scope.Updates.Add(new UpdateOneModel<BsonDocument>(BuildRootFilter(id), Builders<BsonDocument>.Update.Set(LastEventIdField, version)));
		}
	}

	// One statement drops the entity: its root (_eid == _id) and every facet hanging off _eid. Same
	// observable result as the in-memory projection, where facets live on the removed instance.
	void DeleteEntity(Guid id)
	{
		var scope = Scope;
		scope.DroppedEntities.Add(id);
		foreach (var owned in scope.Documents.Where(d => d.Value[EntityIdField].AsGuid == id).Select(d => d.Key).ToList())
		{
			scope.Documents.Remove(owned);
			scope.Changed.Remove(owned);
		}
	}

	/// <summary>
	/// Applies one property change to the write's copy of the document (<paramref name="doc"/>, null when the stream has
	/// none), which the write then stores whole. The element name and the value serializer come from the type's own
	/// <see cref="BsonClassMap"/> — the very class map <see cref="ToDocument"/> serializes through — so the stored field is
	/// byte-identical to what a full re-serialization would have produced, and the write needs no materialized instance at all.
	/// A null value drops the field when the class map says so (the IgnoreIfNull convention), matching
	/// <see cref="ToDocument"/>, which never writes such a field. Never creates: a change to a document that
	/// does not exist (within this stream) is an inconsistency and throws rather than minting a partial
	/// document out of a single field.
	/// </summary>
	void SetField(BsonDocument? doc, Type type, string propertyName, object? value, string what, Guid stampVersion = default)
	{
		// GetMemberMap sees declared members only; an inherited one (a link's TargetId) lives on a base map.
		var classMap = BsonClassMap.LookupClassMap(type);
		var memberMap = classMap.GetMemberMap(propertyName);
		while (memberMap is null && classMap.BaseClassMap is { } baseMap)
		{
			classMap = baseMap;
			memberMap = classMap.GetMemberMap(propertyName);
		}
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

		if (doc is null)
		{
			throw new InvalidOperationException($"Cannot apply a property change to {what}: no document matched in stream {StreamId}.");
		}

		if (value is null && memberMap.IgnoreIfNull)
		{
			doc.Remove(memberMap.ElementName);
		}
		else
		{
			doc[memberMap.ElementName] = SerializeMember(memberMap, CoerceValue(memberMap, value));
		}
		if (stampVersion != default)
		{
			doc[LastEventIdField] = new BsonBinaryData(stampVersion, GuidRepresentation.Standard);
		}
		Touch(doc);
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
		return Task.CompletedTask;
	}

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

	/// <summary>
	/// A batch is its children's events in the one transaction every write already runs in: later
	/// children see earlier ones through the session, a refusal anywhere aborts the whole, and the
	/// expectations are checked against the stored versions first.
	/// </summary>
	public async Task VisitAsync(BatchCommand cmd, CommandHandlerContext ctx)
	{
		// The batch is the unit of intent (and of undo): its own command event carries the children, they emit none.
		var inner = new CommandHandlerContext { Events = ctx.Events, EmitCommandEvents = false };
		foreach (var expectation in cmd.Expectations)
		{
			if (expectation.ExpectedLastEventId != Guid.Empty)
			{
				RequireVersion(expectation.TargetId, expectation.ExpectedLastEventId);
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

	// ---------------------------------------------------------------- IEventVisitor

	public Task BeforeVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;

	public Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx) => ApplyInWriteAsync(() => ApplyObjectPropertyChange(ev));

	void ApplyObjectPropertyChange(ObjectPropertyChangedEvent ev)
	{
		var type = TypeMetadataProvider.GetTypeMetadata(ev.TargetTypeId).Type;
		SetField(FindRoot(ev.TargetId), type, ev.PropertyName, ev.NewValue, $"entity {ev.TargetId}", ev.EventId);
		RunAfterCommit(() =>
		{
			// The live instance is kept in sync when this process holds it; the durable write never needs it.
			if (TryGetTracked(ev.TargetId, out var model))
			{
				ComponentApplyHelpers.ApplyPropertyChange(model, ev.PropertyName, ev.NewValue);
			}
			MarkApplied(ev.TargetId, ev.EventId);
		});
	}

	void MarkApplied(Guid targetId, Guid eventId)
	{
		if (TryGetTracked(targetId, out var model) && _byModel.TryGetValue(model, out var tracked))
		{
			tracked.LastEventId = eventId;
		}
	}

	public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;

	public Task VisitAsync(ComponentAddedEvent ev, EventVisitorContext ctx) => ApplyInWriteAsync(() => ApplyComponentAdd(ev));

	void ApplyComponentAdd(ComponentAddedEvent ev)
	{
		if (ev.ComponentId == ev.TargetId)
		{
			ApplyRootAdd(ev);
		}
		else if (TypeMetadataProvider.IsLinkComponent(ev.ComponentTypeId))
		{
			ApplyLinkAdd(ev);
		}
		else if (TryGetTracked(ev.TargetId, out var containerModel))
		{
			ApplyFacetAddToLiveEntity(ev, ComponentApplyHelpers.ResolveContainer(containerModel, ev.TargetId));
		}
		else
		{
			ApplyFacetAddToStoredEntity(ev);
		}
	}

	/// <summary>
	/// Phase 2 (ECS): a self-owned ROOT COMPONENT is the entity's own data (_id == _eid == entityId), a
	/// self-owned document in the shared Components collection. Always attached; tracking is weak, so it
	/// stays in memory only while someone holds it.
	/// </summary>
	void ApplyRootAdd(ComponentAddedEvent ev)
	{
		var rootType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		object rootModel = ev.Data ?? (TryGetTracked(ev.TargetId, out var tracked) ? tracked : Activator.CreateInstance(rootType)!);
		// Race: another caller may materialize this same id concurrently (the collection enumerator or
		// ResolveObject on a re-delivered add). AttachWithId registers id -> instance atomically and hands
		// back whichever instance won; that one is written, so the tracked instance and the document agree.
		var attached = AttachWithId(rootModel, ev.TargetId, OpenCollection(ev.CollectionId, ev.TargetTypeId));
		if (ReferenceEquals(attached, rootModel) && !_scope.Value!.Attached.Contains(ev.TargetId))
		{
			_scope.Value.Attached.Add(ev.TargetId); // new to tracking: evicted if the transaction aborts
		}
		CreateRoot(ev.TargetId, attached, ev.EventId);
		RunAfterCommit(() => MarkApplied(ev.TargetId, ev.EventId));
	}

	/// <summary>
	/// Nobody here holds the entity: decide on the stored state, read through the session, and load
	/// nothing. Vetoes and activation belong to a live container, so they run only when one is tracked.
	/// </summary>
	void ApplyFacetAddToStoredEntity(ComponentAddedEvent ev)
	{
		var componentType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		if (FindRoot(ev.TargetId) is null)
		{
			throw new InvalidOperationException($"ComponentAddedEvent {ev.EventId}: no entity {ev.TargetId} in stream {StreamId} to add a '{componentType.Name}' to.");
		}
		if (HasUniqueFacet(ev.TargetId, componentType))
		{
			throw new InvalidOperationException($"ComponentAddedEvent {ev.EventId} could not attach a '{componentType.Name}' to {ev.TargetId}: a unique slot it fills is taken.");
		}
		CreateComponent(ev.TargetId, ev.ComponentTypeId, ev.ComponentId, ComponentApplyHelpers.MaterializeComponent(componentType, ev.Data, ev.ComponentId));
		StampVersion(ev.TargetId, ev.EventId);
	}

	void ApplyFacetAddToLiveEntity(ComponentAddedEvent ev, IComponentContainer container)
	{
		var componentType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		var component = ComponentApplyHelpers.MaterializeComponent(componentType, ev.Data, ev.ComponentId);
		// Uniqueness is decided on the stored facets, read through the session, so a facet added earlier
		// in the same write counts; the live collection answers for vetoes and for what it already holds.
		if (!CanAddToLive(container, componentType) || HasUniqueFacet(ev.TargetId, componentType))
		{
			throw new InvalidOperationException(
				$"ComponentAddedEvent {ev.EventId} could not attach a '{componentType.Name}' to container {ev.TargetId} — uniqueness or veto check rejected it.");
		}
		CreateComponent(ev.TargetId, ev.ComponentTypeId, ev.ComponentId, component);
		StampVersion(ev.TargetId, ev.EventId);

		RunAfterCommit(() =>
		{
			if (!container.Components.TryAdd(component))
			{
				throw new InvalidOperationException($"ComponentAddedEvent {ev.EventId}: the stored '{componentType.Name}' facet could not be attached to the live container {ev.TargetId} after commit.");
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
			MarkApplied(ev.TargetId, ev.EventId);
		});
	}

	/// <summary>Whether the entity already stores a facet filling one of the unique slots this type declares ([Component(IsUnique)] on it, a base or an interface).</summary>
	bool HasUniqueFacet(Guid entityId, Type componentType)
	{
		var slots = UniqueSlotsOf(componentType).ToList();
		var taken = false;
		if (slots.Count > 0)
		{
			var names = slots.SelectMany(ConcreteTypeNamesOf).ToHashSet();
			taken = FindFacets(entityId).Any(doc => names.Contains(doc[DiscriminatorField].AsString));
		}
		return taken;
	}

	IEnumerable<Type> UniqueSlotsOf(Type componentType)
	{
		var seen = new HashSet<Type>();
		var pending = new Stack<Type>();
		pending.Push(componentType);
		while (pending.Count > 0)
		{
			var type = pending.Pop();
			if (!seen.Add(type))
			{
				continue;
			}
			if (type.GetCustomAttribute<ComponentAttribute>(inherit: false) is { IsUnique: true })
			{
				yield return type;
			}
			if (type.BaseType is { } baseType && baseType != typeof(object))
			{
				pending.Push(baseType);
			}
			foreach (var contract in type.GetInterfaces())
			{
				pending.Push(contract);
			}
		}
	}

	public Task VisitAsync(ComponentPropertyChangedEvent ev, EventVisitorContext ctx) => ApplyInWriteAsync(() => ApplyComponentPropertyChange(ev));

	void ApplyComponentPropertyChange(ComponentPropertyChangedEvent ev)
	{
		// Durable persistence still forks: a root component is its entity's own document, a facet
		// lives in the shared Components collection. Either way it is a single-field update.
		var componentType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		var isLink = typeof(Link).IsAssignableFrom(componentType);
		if (isLink)
		{
			GuardLinkChange(ev, componentType);
		}
		if (ev.ComponentId == ev.TargetId)
		{
			SetField(FindRoot(ev.TargetId), componentType, ev.PropertyName, ev.NewValue, $"root component {ev.TargetId}", ev.EventId);
		}
		else
		{
			SetField(FindOwned(ev.TargetId, componentType, ev.ComponentId), componentType, ev.PropertyName, ev.NewValue, $"component {ev.ComponentId} of {ev.TargetId}");
			StampVersion(ev.TargetId, ev.EventId);
		}
		RunAfterCommit(() =>
		{
			// The live instance (root entity, or the facet on its tracked container) is kept in sync when
			// this process holds it; the durable write never needed it. A tracked container that lacks
			// the facet is still an inconsistency and throws (ResolveTarget).
			// A link is re-read from Mongo on every index query; only a container holds one live.
			if (TryGetTracked(ev.TargetId, out var trackedModel) && (!isLink || trackedModel is IComponentContainer))
			{
				var target = ComponentApplyHelpers.ResolveTarget(trackedModel, ev, TypeMetadataProvider);
				ComponentApplyHelpers.ApplyPropertyChange(target, ev.PropertyName, ev.NewValue);
			}
			MarkApplied(ev.TargetId, ev.EventId);
		});
	}

	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx) => ApplyInWriteAsync(() => ApplyComponentDelete(ev));

	void ApplyComponentDelete(ComponentDeletedEvent ev)
	{
		if (ev.ComponentId == ev.TargetId)
		{
			// Root delete: the entity, its facets and the links it owns go in one DeleteMany by _eid; the
			// links pointing at it are removed from their owners too.
			var incoming = DeleteLinksTargeting(ev.TargetId, ev.EventId);
			_scope.Value!.Removed.UnionWith(incoming.Select(link => link.LinkId));
			DeleteEntity(ev.TargetId);
			RunAfterCommit(() =>
			{
				foreach (var (ownerId, linkId) in incoming)
				{
					RemoveLiveFacet(ownerId, linkId);
				}
				Evict(ev.TargetId);
			});
		}
		else
		{
			var componentType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
			if (FindOwned(ev.TargetId, componentType, ev.ComponentId) is null)
			{
				throw new InvalidOperationException($"ComponentDeletedEvent {ev.EventId}: no '{componentType.Name}' facet {ev.ComponentId} on {ev.TargetId} in stream {StreamId}.");
			}
			Delete(ev.ComponentId);
			_scope.Value!.Removed.Add(ev.ComponentId);
			StampVersion(ev.TargetId, ev.EventId);
			RunAfterCommit(() =>
			{
				// BypassRemove rather than Remove: when the container is wrapped in
				// StoreBoundComponentsCollection, the ICollection<T>.Remove path emits a command. The
				// projection is APPLYING an event, so it must skip the command channel — otherwise it would
				// generate a recursive delete command.
				if (TryGetTracked(ev.TargetId, out var trackedModel) && trackedModel is IComponentContainer container
					&& container.Components.FirstOrDefault(c => c is IIdentifiable<Guid> identified && identified.Id == ev.ComponentId) is { } component
					&& !container.Components.BypassRemove(component))
				{
					throw new InvalidOperationException($"ComponentDeletedEvent {ev.EventId}: the live container refused to remove the deleted facet {ev.ComponentId}.");
				}
				MarkApplied(ev.TargetId, ev.EventId);
			});
		}
	}

	/// <summary>
	/// A link component lands in the shared Components collection under its owner (the source): the owner
	/// is stamped as <see cref="Link.SourceId"/> and must exist, a structural duplicate is refused, and its
	/// unique slots are checked like any facet's. A live container gets it after commit.
	/// </summary>
	void ApplyLinkAdd(ComponentAddedEvent ev)
	{
		var linkType = TypeMetadataProvider.GetTypeMetadata(ev.ComponentTypeId).Type;
		var link = (Link)ComponentApplyHelpers.MaterializeComponent(linkType, ev.Data, ev.ComponentId);
		link.SourceId = ev.TargetId;
		if (FindRoot(ev.TargetId) is null)
		{
			throw new InvalidOperationException($"ComponentAddedEvent {ev.EventId}: no entity {ev.TargetId} in stream {StreamId} to own a '{linkType.Name}' link.");
		}
		if (FindStructuralDuplicate(link, ev.ComponentId) is { } duplicateId)
		{
			throw new InvalidOperationException(
				$"ComponentAddedEvent {ev.EventId} could not register a '{linkType.Name}' link — an equivalent link ({duplicateId}) already exists between the same endpoints.");
		}
		TryGetTracked(ev.TargetId, out var ownerModel);
		var container = ownerModel as IComponentContainer;
		if (HasUniqueFacet(ev.TargetId, linkType) || (container is not null && !CanAddToLive(container, linkType)))
		{
			throw new InvalidOperationException($"ComponentAddedEvent {ev.EventId} could not attach a '{linkType.Name}' link to {ev.TargetId}: uniqueness or veto check rejected it.");
		}
		CreateComponent(ev.TargetId, ev.ComponentTypeId, ev.ComponentId, link);
		StampVersion(ev.TargetId, ev.EventId);
		RunAfterCommit(() =>
		{
			if (container is not null && !container.Components.TryAdd(link))
			{
				throw new InvalidOperationException($"ComponentAddedEvent {ev.EventId}: the stored '{linkType.Name}' link could not be attached to the live container {ev.TargetId} after commit.");
			}
			((IBindableComponent)link).AttachToContainer(this, ev.TargetId, ev.TargetTypeId, ev.CollectionId);
			MarkApplied(ev.TargetId, ev.EventId);
		});
	}

	/// <summary>
	/// A link's owner never changes; a new target must not make it a duplicate of another link. Read from
	/// the write's copy of the stored document, so an earlier change in the same write counts.
	/// </summary>
	void GuardLinkChange(ComponentPropertyChangedEvent ev, Type linkType)
	{
		LinkApplyHelpers.GuardSourceUnchanged(ev);
		if (ev.PropertyName == nameof(Link.TargetId))
		{
			var doc = FindOwned(ev.TargetId, linkType, ev.ComponentId)
				?? throw new InvalidOperationException($"ComponentPropertyChangedEvent {ev.EventId}: no '{linkType.Name}' link {ev.ComponentId} on {ev.TargetId} in stream {StreamId}.");
			var retargeted = (Link)FromDocument(doc);
			retargeted.SourceId = ev.TargetId;
			retargeted.TargetId = ev.NewValue is Guid target ? target : Guid.Parse(Convert.ToString(ev.NewValue, System.Globalization.CultureInfo.InvariantCulture)!);
			if (FindStructuralDuplicate(retargeted, ev.ComponentId) is { } duplicateId)
			{
				throw new InvalidOperationException(
					$"ComponentPropertyChangedEvent {ev.EventId} could not retarget '{linkType.Name}' link {ev.ComponentId} — an equivalent link ({duplicateId}) already exists between the new endpoints.");
			}
		}
	}

	/// <summary>Deletes every link pointing at <paramref name="entityId"/> from its owner and returns which ones went.</summary>
	IReadOnlyList<(Guid OwnerId, Guid LinkId)> DeleteLinksTargeting(Guid entityId, Guid eventId)
	{
		Load([entityId]);
		var linkTypes = ConcreteTypeNamesOf(typeof(Link)).ToHashSet();
		var incoming = Scope.Documents.Values
			.Where(doc => linkTypes.Contains(doc[DiscriminatorField].AsString) && ReadGuid(doc, nameof(Link.TargetId)) == entityId && doc[EntityIdField].AsGuid != entityId)
			.Select(doc => (OwnerId: doc[EntityIdField].AsGuid, LinkId: doc["_id"].AsGuid))
			.ToArray();
		foreach (var (ownerId, linkId) in incoming)
		{
			Delete(linkId);
			StampVersion(ownerId, eventId);
		}
		return incoming;
	}

	/// <summary>Drops a deleted facet from its owner's live Components, when this process holds the owner.</summary>
	void RemoveLiveFacet(Guid ownerId, Guid componentId)
	{
		if (TryGetTracked(ownerId, out var model) && model is IComponentContainer container
			&& container.Components.FirstOrDefault(c => c is IIdentifiable<Guid> identified && identified.Id == componentId) is { } component)
		{
			container.Components.BypassRemove(component);
		}
	}

	/// <summary>
	/// Looks for an existing link with the same <see cref="Link.StructuralKey"/> as <paramref name="link"/>,
	/// excluding <paramref name="excludeId"/> itself. The candidates are the links between the same endpoints
	/// in either order (an undirected duplicate can arrive reversed), read from the write's documents of the
	/// source: a link is owned by its source, so the source's own links hold one order and the links pointing
	/// at it the other. Structural equality is decided by <see cref="Link.StructuralKey"/>.
	/// </summary>
	Guid? FindStructuralDuplicate(Link link, Guid excludeId)
	{
		Load([link.SourceId]);
		var linkTypes = ConcreteTypeNamesOf(link.GetType()).ToHashSet();
		return Scope.Documents.Values
			.Where(doc => doc["_id"].AsGuid != excludeId && linkTypes.Contains(doc[DiscriminatorField].AsString))
			.Where(doc => false
				|| (ReadGuid(doc, nameof(Link.SourceId)) == link.SourceId && ReadGuid(doc, nameof(Link.TargetId)) == link.TargetId)
				|| (ReadGuid(doc, nameof(Link.SourceId)) == link.TargetId && ReadGuid(doc, nameof(Link.TargetId)) == link.SourceId)
			)
			.Where(doc => ((Link)FromDocument(doc)).StructuralKey.Equals(link.StructuralKey))
			.Select(doc => (Guid?)doc["_id"].AsGuid)
			.FirstOrDefault();
	}

	public Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;

	// ---------------------------------------------------------------- Component apply helpers


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
		Upsert(doc);
	}

	/// <summary>The container's facet documents: the write's copies inside a write, one query by <c>(_sid, _eid)</c> outside one.</summary>
	IEnumerable<BsonDocument> ReadFacets(Guid containerId)
	{
		IEnumerable<BsonDocument> facets;
		if (_scope.Value is not null)
		{
			facets = FindFacets(containerId);
		}
		else
		{
			// Exclude the container itself: its root document also has _eid == _id but is not one of its facets.
			facets = FindDocuments(Components, Builders<BsonDocument>.Filter.And(StreamFilter, Builders<BsonDocument>.Filter.Eq(EntityIdField, containerId), Builders<BsonDocument>.Filter.Ne("_id", containerId)));
		}
		return facets;
	}

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
	void LoadComponentsInto(IComponentContainer container, Guid containerId, Guid containerCollectionId, IEnumerable<BsonDocument> facets)
	{
		foreach (var doc in facets)
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

	// ---------------------------------------------------------------- IComponentIndex

	/// <summary>One query on <c>_id</c> + stream for the facet's owner, then the owner itself (tracked, or one more <c>_id</c> query that also loads its facets).</summary>
	public ComponentHit? FindComponent(Guid componentId)
	{
		ComponentHit? hit = null;
		var doc = componentId == default ? null : FindDocument(Components, BuildRootFilter(componentId), Builders<BsonDocument>.Projection.Include(EntityIdField));
		if (doc is not null && doc.TryGetValue(EntityIdField, out var owner) && owner.AsGuid != componentId
			&& ResolveObject(owner.AsGuid) is IComponentContainer container
			&& container.Components.FirstOrDefault(c => c is IIdentifiable<Guid> identified && identified.Id == componentId) is { } component)
		{
			hit = new ComponentHit(container, component);
		}
		return hit;
	}

	/// <summary>
	/// Roots by <c>(_sid, _t, _id)</c>, in id order through a cursor: a page reads one batch, never the stream. A facet
	/// filter first collects the owners carrying that facet type, by <c>(_sid, _t, _eid)</c>; a text filter the owners holding its words, by <c>(_sid, $** text)</c>.
	/// </summary>
	public IEnumerable<object> FindObjects(ObjectQuery query)
	{
		var filters = Builders<BsonDocument>.Filter;
		var parts = new List<FilterDefinition<BsonDocument>> { StreamFilter, filters.In(DiscriminatorField, query.Types.SelectMany(ConcreteTypeNamesOf).Distinct()) };
		if (query.After != default)
		{
			parts.Add(filters.Gt("_id", query.After));
		}
		if (query.Ids is { } ids)
		{
			parts.Add(filters.In("_id", ids));
		}
		if (query.Has is { } has)
		{
			parts.Add(filters.In("_id", OwnersOf(has)));
		}
		if (query.Words is { Count: > 0 } words)
		{
			parts.Add(filters.In("_id", FindOwnersMentioning(words)));
		}
		var find = FlushSession() is { } session ? Components.Find(session, filters.And(parts)) : Components.Find(filters.And(parts));
		using var cursor = find.Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToCursor();
		while (cursor.MoveNext())
		{
			var page = cursor.Current.ToList();
			// One facet query per cursor batch, for the objects this process does not hold yet.
			var facets = ReadFacetsOf(page.Select(doc => doc["_id"].AsGuid).Where(id => !TryGetTracked(id, out _)).ToList());
			foreach (var doc in page)
			{
				var id = doc["_id"].AsGuid;
				yield return TryGetTracked(id, out var tracked) ? tracked : AttachLoaded(doc, id, collection: null, facets[id]);
			}
		}
	}

	/// <summary>The facet documents of these containers, by container: one query by <c>(_sid, _eid)</c>, none for no containers.</summary>
	ILookup<Guid, BsonDocument> ReadFacetsOf(IReadOnlyCollection<Guid> containerIds)
	{
		var facets = Array.Empty<BsonDocument>().ToLookup(doc => default(Guid));
		if (containerIds.Count > 0)
		{
			facets = FindDocuments(Components, Builders<BsonDocument>.Filter.And(StreamFilter, Builders<BsonDocument>.Filter.In(EntityIdField, containerIds)))
				.Where(doc => doc["_id"].AsGuid != doc[EntityIdField].AsGuid)
				.ToLookup(doc => doc[EntityIdField].AsGuid);
		}
		return facets;
	}

	/// <summary>
	/// The object each id belongs to: one query by <c>_id</c> for the owners of the ids this process does not hold, one by
	/// <c>(_sid, _eid)</c> for those owners' roots and facets, whatever the count.
	/// </summary>
	public IReadOnlyDictionary<Guid, object> FindOwners(IEnumerable<Guid> ids)
	{
		var asked = ids.Where(id => id != default).Distinct().ToList();
		var ownerIds = new Dictionary<Guid, Guid>();
		var owners = new Dictionary<Guid, object>();
		var unknown = asked.Where(id => !TryGetTracked(id, out _)).ToList();
		if (unknown.Count > 0)
		{
			foreach (var doc in FindDocuments(Components, Builders<BsonDocument>.Filter.And(StreamFilter, Builders<BsonDocument>.Filter.In("_id", unknown)), Builders<BsonDocument>.Projection.Include(EntityIdField)))
			{
				ownerIds[doc["_id"].AsGuid] = doc[EntityIdField].AsGuid;
			}
		}
		var untracked = ownerIds.Values.Distinct().Where(id => !TryGetTracked(id, out _)).ToList();
		if (untracked.Count > 0)
		{
			foreach (var entity in FindDocuments(Components, Builders<BsonDocument>.Filter.And(StreamFilter, Builders<BsonDocument>.Filter.In(EntityIdField, untracked))).GroupBy(doc => doc[EntityIdField].AsGuid))
			{
				if (entity.FirstOrDefault(doc => doc["_id"].AsGuid == entity.Key) is { } root)
				{
					owners[entity.Key] = TryGetTracked(entity.Key, out var raced) ? raced : AttachLoaded(root, entity.Key, collection: null, entity.Where(doc => doc != root));
				}
			}
		}
		var found = new Dictionary<Guid, object>();
		foreach (var id in asked)
		{
			var ownerId = ownerIds.TryGetValue(id, out var stored) ? stored : id;
			if (owners.TryGetValue(ownerId, out var owner) || TryGetTracked(ownerId, out owner))
			{
				found[id] = owner;
			}
		}
		return found;
	}

	// The ids of the entities carrying a facet of this type (or a subtype): a distinct over the (_sid, _t, _eid) index, read when the outer query renders.
	IEnumerable<Guid> OwnersOf(Type facetType)
	{
		var filter = Builders<BsonDocument>.Filter.And(StreamFilter, Builders<BsonDocument>.Filter.In(DiscriminatorField, ConcreteTypeNamesOf(facetType)));
		var distinct = FlushSession() is { } session ? Components.Distinct<BsonValue>(session, EntityIdField, filter) : Components.Distinct<BsonValue>(EntityIdField, filter);
		return distinct.ToEnumerable().Select(v => v.AsGuid);
	}

	/// <summary>
	/// The ids of the entities whose root or facets hold every word: one <c>(_sid, $** text)</c> query per word, each word a
	/// phrase so it matches whole, owners intersected across words; stops at the first word nothing holds.
	/// </summary>
	HashSet<Guid> FindOwnersMentioning(IReadOnlyList<string> words)
	{
		var owners = FindOwnersHolding(words[0]);
		for (var i = 1; i < words.Count && owners.Count > 0; i++)
		{
			owners.IntersectWith(FindOwnersHolding(words[i]));
		}
		return owners;
	}

	// The owners (_eid) of the stream's documents holding this word, read from the text index.
	HashSet<Guid> FindOwnersHolding(string word)
	{
		var filter = Builders<BsonDocument>.Filter.And(StreamFilter, Builders<BsonDocument>.Filter.Text("\"" + word + "\""));
		var find = FlushSession() is { } session ? Components.Find(session, filter) : Components.Find(filter);
		return find.Project(Builders<BsonDocument>.Projection.Include(EntityIdField)).ToEnumerable().Select(d => d[EntityIdField].AsGuid).ToHashSet();
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
		var bindable = (IBindableComponent)link;
		// The component id is generated hidden state, not a serialized member — restore it from _id.
		bindable.SetComponentId(doc["_id"].AsGuid);
		var ownerId = doc[EntityIdField].AsGuid;
		var ownerType = ResolveObject(ownerId)?.GetType();
		bindable.AttachToContainer(this, ownerId, ownerType is null ? Guid.Empty : TypeMetadataProvider.GetTypeMetadata(ownerType).TypeId, Guid.Empty);
		return link;
	}

	/// <summary>Every link document of the stream: any concrete type deriving from <see cref="Link"/>.</summary>
	FilterDefinition<BsonDocument> AllLinksFilter => Builders<BsonDocument>.Filter.And(StreamFilter, BuildLinkTypeFilter(typeof(Link)));

	IReadOnlyCollection<Link> ILinkIndex.Links
	{
		get
		{
			return FindDocuments(Components, AllLinksFilter)
				.Select(LoadLink)
				.ToArray();
		}
	}

	IReadOnlyList<Link> ILinkIndex.LinksAt(Guid nodeId, LinkEnd end, Type linkType)
	{
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
		return FindDocuments(Components, Builders<BsonDocument>.Filter.And(StreamFilter, BuildLinkTypeFilter(linkType), endpointFilter))
			.Select(LoadLink)
			.ToArray();
	}

	IReadOnlyList<Link> ILinkIndex.LinksBetween(Guid a, Guid b, Type linkType)
	{
		var endpointFilter = Builders<BsonDocument>.Filter.Or(
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", a), Builders<BsonDocument>.Filter.Eq("TargetId", b)),
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", b), Builders<BsonDocument>.Filter.Eq("TargetId", a)));
		return FindDocuments(Components, Builders<BsonDocument>.Filter.And(StreamFilter, BuildLinkTypeFilter(linkType), endpointFilter))
			.Select(LoadLink)
			.ToArray();
	}

	bool ILinkIndex.TryGetByKey(LinkKey key, out Link? link)
	{
		var linkType = key.LinkType;
		var endpointFilter = Builders<BsonDocument>.Filter.Or(
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", key.X), Builders<BsonDocument>.Filter.Eq("TargetId", key.Y)),
			Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("SourceId", key.Y), Builders<BsonDocument>.Filter.Eq("TargetId", key.X)));

		foreach (var doc in FindDocuments(Components, Builders<BsonDocument>.Filter.And(StreamFilter, BuildLinkTypeFilter(linkType), endpointFilter)))
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
		var filter = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("_id", linkId), AllLinksFilter);
		var doc = FindDocument(Components, filter);
		if (doc is null)
		{
			link = null;
			return false;
		}
		link = LoadLink(doc); // concrete type resolved from the "_t" discriminator
		return true;
	}
}
