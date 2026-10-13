using System.Collections;
using MongoDB.Bson;
using MongoDB.Driver;
using Synqra.BinarySerializer;

namespace Synqra.Projection.MongoDb;

/// <summary>
/// Non-generic base so the projection can cache collections by id and activate them reflectively.
/// <paramref name="streamId"/> only satisfies <see cref="StoreCollection"/>'s non-default
/// validation at construction time — it is <b>not</b> what any Mongo query below actually filters
/// by (this instance is cached per collection id in <see cref="MongoProjection"/> and reused across
/// every stream a request for this type happens to be scoped to; see <see cref="MongoStoreCollection{T}.StreamFilter"/>,
/// which reads the ambient <see cref="SynqraStreamContext.Current"/> fresh on every query instead).
/// </summary>
internal abstract class MongoStoreCollection : StoreCollection
{
	protected MongoStoreCollection(
		  IObjectStore store
		, Guid streamId
		, Guid collectionId
		, ISbxSerializerFactory serializerFactory
		)
		: base(store, streamId, collectionId, serializerFactory)
	{
	}
}

/// <summary>
/// A Synqra collection over the roots of one model type in the shared <c>Components</c> Mongo collection
/// (<c>_t == T</c>, <c>_id == _eid</c>). <c>Add</c> attaches the model and submits an
/// <see cref="AddComponentCommand"/>; enumeration queries the documents and materializes them into
/// tracked model instances.
/// </summary>
internal sealed class MongoStoreCollection<T> : MongoStoreCollection, ISynqraCollection<T>
	where T : class
{
	readonly MongoProjection _projection;

	public MongoStoreCollection(
		  IObjectStore store
		, Guid streamId
		, Guid collectionId
		, ISbxSerializerFactory serializerFactory
		)
		: base(store, streamId, collectionId, serializerFactory)
	{
		_projection = (MongoProjection)store;
	}

	public override Type Type => typeof(T);

	// _projection.StreamId (ambient, read live) — not the base StoreCollection.StreamId field,
	// which is only whatever was in the ambient context the moment this collection was first
	// constructed and cached (see MongoProjection.GetCollection's per-collectionId cache). This
	// instance is reused across every stream, so every actual query must re-read the ambient
	// value fresh rather than trust what got baked in at construction.
	FilterDefinition<BsonDocument> StreamFilter => Builders<BsonDocument>.Filter.Eq("_sid", _projection.StreamId);

	// Discriminators of T and every registered subtype ("_t" is the actual type). Recomputed only when the
	// registry has grown since last time; the registry only ever grows.
	string[] _discriminators = [];
	int _discriminatorsFor = -1;

	string[] Discriminators
	{
		get
		{
			var registered = _projection.TypeMetadataProvider.RegisteredTypes;
			if (registered.Count != _discriminatorsFor)
			{
				_discriminators = registered.Where(m => typeof(T).IsAssignableFrom(m.Type)).Select(m => m.Type.Name).ToArray();
				_discriminatorsFor = registered.Count;
			}
			return _discriminators;
		}
	}

	// This stream's roots of T or any registered subtype — facets and other types share the same physical
	// collection, and a type is either a root type or a facet type, so "_t" alone selects roots.
	FilterDefinition<BsonDocument> RootsFilter => Builders<BsonDocument>.Filter.And(
		StreamFilter,
		Builders<BsonDocument>.Filter.In("_t", Discriminators));

	public int Count => (int)_projection.Components.CountDocuments(RootsFilter);

	bool ICollection<T>.IsReadOnly => false;

	void ICollection<T>.Add(T item)
	{
		var id = _projection.Attach(item, CollectionId);
		var typeId = _projection.TypeMetadataProvider.GetTypeMetadata(typeof(T)).TypeId;
		// Phase 2 (ECS): GetCollection<T>().Add always creates an ENTITY — a self-owned root component
		// (ComponentId == TargetId == entity id, _id == _eid == entityId). No object-vs-component branch.
		var task = Store.SubmitCommandAsync(new AddComponentCommand
		{
			StreamId = _projection.StreamId,
			CollectionId = CollectionId,
			TargetTypeId = typeId,
			TargetId = id,
			TargetObject = item,
			ComponentTypeId = typeId,
			ComponentId = id,
			Data = item,
		});
		task.GetAwaiter().GetResult();
	}

	IEnumerator<T> IEnumerable<T>.GetEnumerator()
	{
		foreach (var doc in _projection.FindDocuments(_projection.Components, RootsFilter))
		{
			var id = doc["_id"].AsGuid;
			if (_projection.TryGetTracked(id, out var existing))
			{
				yield return (T)existing;
				continue;
			}
			yield return (T)_projection.AttachLoaded(doc, id);
		}
	}

	IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)this).GetEnumerator();

	public override IEnumerator GetEnumerator() => ((IEnumerable<T>)this).GetEnumerator();

	void ICollection<T>.Clear() => throw new NotSupportedException();
	bool ICollection<T>.Contains(T item) => throw new NotSupportedException();
	void ICollection<T>.CopyTo(T[] array, int arrayIndex)
	{
		foreach (var item in (IEnumerable<T>)this)
		{
			array[arrayIndex++] = item;
		}
	}
	bool ICollection<T>.Remove(T item) => throw new NotSupportedException();
}
