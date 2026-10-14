using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;
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

	public MongoAppendStorage([FromKeyedServices("Synqra.AppendStorage")] IMongoCollection<T> collection)
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

	/// <summary>
	/// Appends a batch whole or not at all: one transaction on the log's own cluster (which must be a
	/// replica set for batches). Idempotent: ids already stored are skipped, so appending the same
	/// batch again (a caller's retried write) is a no-op. A single event needs no transaction.
	/// </summary>
	public async Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
	{
		var list = items as IReadOnlyList<T> ?? items.ToArray();
		if (list.Count == 1)
		{
			await AppendAsync(list[0], cancellationToken);
		}
		else if (list.Count > 1)
		{
			await _collection.Database.Client.RunInTransactionAsync(async (s, ct) =>
			{
				var ids = list.Select(ReadKey).ToList();
				var stored = (await _collection.Find(s, Builders<T>.Filter.In("_id", ids)).ToListAsync(ct)).Select(ReadKey).ToHashSet(); // normally empty: only a retry finds any
				var missing = list.Where(item => !stored.Contains(ReadKey(item))).ToList();
				if (missing.Count > 0)
				{
					await _collection.InsertManyAsync(s, missing, new InsertManyOptions { IsOrdered = true }, ct);
				}
			}, cancellationToken);
		}
	}

	/// <summary>The item's key, read through the member its class map maps to <c>_id</c> (see the key contract above).</summary>
	TKey ReadKey(T item)
	{
		var idMember = BsonClassMap.LookupClassMap(item.GetType()).IdMemberMap
			?? throw new InvalidOperationException($"{item.GetType().Name} has no member mapped to _id; the Mongo log needs one (see MongoEventClassMaps).");
		return (TKey)idMember.Getter(item);
	}

	public async Task<T> GetAsync(TKey key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
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
