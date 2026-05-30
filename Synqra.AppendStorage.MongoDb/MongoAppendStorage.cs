using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// MongoDB-backed append storage for the Synqra event log. Events are stored as
/// native, queryable BSON documents (one per event) using the class maps in
/// <see cref="MongoEventClassMaps"/> — not opaque blobs. The document <c>_id</c> is
/// the event's key (a v7 GUID, so insertion order ≈ time order ≈ <c>_id</c> order),
/// which gives a stable replay sequence without a separate ordering column.
/// <para>
/// Append-only: <see cref="AppendAsync"/> inserts; there is no update path. A
/// duplicate key (same event appended twice) is treated as idempotent and ignored,
/// matching the "events are immutable facts" model.
/// </para>
/// </summary>
public sealed class MongoAppendStorage<T, TKey> : IAppendStorage<T, TKey>
    where T : class
    where TKey : notnull
{
    readonly IMongoCollection<T> _collection;
    readonly Func<T, TKey> _getKey;

    public MongoAppendStorage(IMongoCollection<T> collection, Func<T, TKey> getKey)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _getKey = getKey ?? throw new ArgumentNullException(nameof(getKey));
    }

    public async Task AppendAsync(T item, CancellationToken cancellationToken = default)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }
        try
        {
            await _collection.InsertOneAsync(item, options: null, cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Same event id appended twice — events are immutable facts, so re-appending
            // the same fact is a no-op rather than an error (idempotent writes).
        }
    }

    public async Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        var list = items as IReadOnlyList<T> ?? items.ToArray();
        if (list.Count == 0)
        {
            return;
        }
        try
        {
            await _collection.InsertManyAsync(list, new InsertManyOptions { IsOrdered = true }, cancellationToken);
        }
        catch (MongoBulkWriteException ex) when (ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            // Whole batch already present — idempotent replay of an already-stored batch.
        }
    }

    public async Task<T> GetAsync(TKey key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filter = Builders<T>.Filter.Eq("_id", BsonValue.Create(key));
        var found = await _collection.Find(filter).FirstOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            throw new KeyNotFoundException($"Event with key '{key}' was not found");
        }
        return found;
    }

    public async IAsyncEnumerable<T> GetAllAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filterBuilder = Builders<T>.Filter;
        var filter = Equals(from, default(TKey))
            ? filterBuilder.Empty
            : filterBuilder.Gte("_id", BsonValue.Create(from));

        // Replay in id order — v7 GUID ids are time-ordered, so this is append order.
        var sort = Builders<T>.Sort.Ascending("_id");
        using var cursor = await _collection.Find(filter).Sort(sort).ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var doc in cursor.Current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return doc;
            }
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask; // Mongo writes are durable on ack

    public void Dispose()
    {
        // The IMongoClient behind the collection is owned by DI, not this storage.
    }

    public ValueTask DisposeAsync() => default;
}
