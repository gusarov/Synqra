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
/// This first implementation covers the object lifecycle (create + property change) — enough for the
/// store contract. Components and wires are not materialized here yet (see the in-memory projection for
/// the reference behaviour).
/// </para>
/// </summary>
public sealed class MongoProjection : IObjectStore, IProjection
{
	static MongoProjection()
	{
		AppContext.SetSwitch("Synqra.GuidExtensions.ValidateNamespaceIdHashChain", false);
	}

	readonly IMongoDatabase _database;
	readonly ISbxSerializerFactory _serializerFactory;
	readonly IAppendStorage? _eventStorage;
	readonly JsonSerializerOptions _jsonSerializerOptions;

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
		)
	{
		_database = database ?? throw new ArgumentNullException(nameof(database));
		_serializerFactory = serializerFactory;
		TypeMetadataProvider = typeMetadataProvider;
		_eventStorage = eventStorage;
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
	}

	internal bool TryGetTracked(Guid id, out object model) => _byId.TryGetValue(id, out model!);

	internal IMongoCollection<BsonDocument> MongoCollectionFor(Type type, string collectionName)
		=> _database.GetCollection<BsonDocument>(MongoCollectionName(type, collectionName));

	internal object FromDocument(BsonDocument doc, Type type)
	{
		var clone = (BsonDocument)doc.DeepClone();
		clone.Remove("_id");
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
	public Task VisitAsync(AddComponentCommand cmd, CommandHandlerContext ctx) => throw new NotImplementedException("MongoProjection does not materialize components yet.");
	public Task VisitAsync(ChangeComponentPropertyCommand cmd, CommandHandlerContext ctx) => throw new NotImplementedException("MongoProjection does not materialize components yet.");
	public Task VisitAsync(DeleteComponentCommand cmd, CommandHandlerContext ctx) => throw new NotImplementedException("MongoProjection does not materialize components yet.");

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
	public Task VisitAsync(ComponentAddedEvent ev, EventVisitorContext ctx) => throw new NotImplementedException("MongoProjection does not materialize components yet.");
	public Task VisitAsync(ComponentPropertyChangedEvent ev, EventVisitorContext ctx) => throw new NotImplementedException("MongoProjection does not materialize components yet.");
	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx) => throw new NotImplementedException("MongoProjection does not materialize components yet.");
	public Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
}
