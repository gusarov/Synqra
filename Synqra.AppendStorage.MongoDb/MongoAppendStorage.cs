using System.Runtime.CompilerServices;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// MongoDB-backed append storage for the Synqra event log. Events are stored as
/// native, queryable BSON documents (one per event) using the class maps in
/// <see cref="MongoEventClassMaps"/> — not opaque blobs. The document <c>_id</c> is
/// the event's key (a v7 GUID, so insertion order ≈ time order ≈ <c>_id</c> order),
/// which gives a stable replay sequence without a separate ordering column.
/// <para>
/// <b>Key contract:</b> the item's key field must be mapped to <c>_id</c> in its BSON
/// class map (<see cref="MongoEventClassMaps"/> maps <c>EventId → _id</c> for the
/// <see cref="Event"/> hierarchy). The storage queries and orders by <c>_id</c>
/// directly, so it never needs a key-extraction delegate — Mongo reads the key from
/// the document itself on insert.
/// </para>
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

    public MongoAppendStorage(IMongoCollection<T> collection)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
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
            // IsOrdered = false: a duplicate in the middle of the batch must NOT stop the
            // remaining new events from being inserted. With unordered inserts Mongo tries
            // every document and reports the failures; we swallow duplicate-key errors
            // (idempotent re-append) and rethrow anything else.
            await _collection.InsertManyAsync(list, new InsertManyOptions { IsOrdered = false }, cancellationToken);
        }
        catch (MongoBulkWriteException ex) when (ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            // Every failure was a duplicate — the non-duplicate documents were still
            // inserted (unordered), so this is a fully idempotent outcome.
        }
    }

    public async Task<T> GetAsync(TKey key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Typed filter so the key's serializer (e.g. the Standard GuidSerializer) renders
        // the value the same way it is stored in _id — no representation mismatch.
        var filter = Builders<T>.Filter.Eq("_id", key);
        var found = await _collection.Find(filter).FirstOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            throw new KeyNotFoundException($"{typeof(T).Name} with key '{key}' was not found");
        }
        return found;
    }

    public async IAsyncEnumerable<T> GetAllAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filterBuilder = Builders<T>.Filter;
        // `from` is TKey? — for the common Guid key this defaults to null, which means
        // "from the beginning". Check for null explicitly (Equals(null, default(Guid))
        // would be false and wrongly build `_id >= null`). Use a typed filter so the
        // bound serializes with the same representation as the stored _id.
        var filter = from is null
            ? filterBuilder.Empty
            : filterBuilder.Gte("_id", (TKey)from);

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
