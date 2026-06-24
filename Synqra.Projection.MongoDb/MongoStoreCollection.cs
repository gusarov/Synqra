using System.Collections;
using MongoDB.Bson;
using MongoDB.Driver;
using Synqra.BinarySerializer;

namespace Synqra.Projection.MongoDb;

/// <summary>Non-generic base so the projection can cache collections by id and activate them reflectively.</summary>
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
/// A Synqra collection backed by a MongoDB collection of documents. <c>Add</c> attaches the model and
/// submits a <see cref="CreateObjectCommand"/>; enumeration queries the documents and materializes them
/// into tracked model instances.
/// </summary>
internal sealed class MongoStoreCollection<T> : MongoStoreCollection, ISynqraCollection<T>
	where T : class
{
	readonly MongoProjection _projection;
	readonly IMongoCollection<BsonDocument> _mongo;

	public MongoStoreCollection(
		  IObjectStore store
		, Guid streamId
		, Guid collectionId
		, ISbxSerializerFactory serializerFactory
		, IMongoCollection<BsonDocument> mongo
		)
		: base(store, streamId, collectionId, serializerFactory)
	{
		_projection = (MongoProjection)store;
		_mongo = mongo;
	}

	public override Type Type => typeof(T);

	public int Count => (int)_mongo.CountDocuments(FilterDefinition<BsonDocument>.Empty);

	bool ICollection<T>.IsReadOnly => false;

	void ICollection<T>.Add(T item)
	{
		var id = _projection.Attach(item, CollectionId);
		var task = Store.SubmitCommandAsync(new CreateObjectCommand
		{
			StreamId = StreamId,
			CollectionId = CollectionId,
			TargetTypeId = _projection.TypeMetadataProvider.GetTypeMetadata(typeof(T)).TypeId,
			CommandId = GuidExtensions.CreateVersion7(),
			TargetId = id,
			Data = ObjectData.From(item),
			TargetObject = item,
		});
		task.GetAwaiter().GetResult();
	}

	IEnumerator<T> IEnumerable<T>.GetEnumerator()
	{
		foreach (var doc in _mongo.Find(FilterDefinition<BsonDocument>.Empty).ToList())
		{
			var id = doc["_id"].AsGuid;
			if (_projection.TryGetTracked(id, out var existing))
			{
				yield return (T)existing;
				continue;
			}
			var model = (T)_projection.FromDocument(doc);
			_projection.AttachWithId(model, id, CollectionId);
			yield return model;
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
