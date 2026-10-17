using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// The Mongo event store: one native BSON document per event (<see cref="MongoEventClassMaps"/>), keyed by
/// <c>(StreamId, SequenceId)</c> = <c>(_sid, _id)</c> and read in that order through the <c>(_sid, _id)</c> index, so a
/// stream is a key prefix (docs/model.md §9). <see cref="Event.EventId"/> is a unique field: appending the same event
/// again is a no-op. The <see cref="ServerEventLog"/> over it stamps the sequence; registered by
/// <see cref="MongoEventStorageExtensions.AddMongoEventStorage"/>.
/// </summary>
public sealed class MongoEventStorage : IAppendStorage<Event, (Guid Stream, Guid Sequence)>, IEventSequenceTail, IEventStreamReader
{
	const string IdField = "_id";
	const string StreamField = MongoEventClassMaps.StreamIdField;

	readonly IMongoCollection<Event> _collection;
	readonly ILogger _logger;
	readonly Lazy<Task> _indexes;

	public MongoEventStorage([FromKeyedServices] IMongoCollection<Event> collection, ILogger<MongoEventStorage>? logger = null)
	{
		_collection = collection;
		_logger = logger ?? NullLogger<MongoEventStorage>.Instance;
		_indexes = new Lazy<Task>(() => new EventLogIndexes().EnsureAsync(_collection.Database, _collection.CollectionNamespace.CollectionName));
	}

	public async Task AppendAsync(Event item, CancellationToken cancellationToken = default)
	{
		await _indexes.Value;
		try
		{
			await _collection.InsertOneAsync(item, options: null, cancellationToken);
		}
		catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
		{
			// Tolerable: an event is an immutable fact, so the same EventId appended again (a retried write) is already stored.
			_logger.LogDebug(ex, "Event {EventId} is already in the log; the append is a no-op.", item.EventId);
		}
	}

	/// <summary>
	/// A batch lands whole or not at all: one transaction on the log's own cluster (a replica set). Idempotent: events
	/// already stored (by EventId) are skipped, so a retried batch is a no-op.
	/// </summary>
	public async Task AppendBatchAsync(IEnumerable<Event> items, CancellationToken cancellationToken = default)
	{
		var list = items as IReadOnlyList<Event> ?? items.ToArray();
		if (list.Count == 1)
		{
			await AppendAsync(list[0], cancellationToken);
		}
		else if (list.Count > 1)
		{
			await _indexes.Value;
			await _collection.Database.Client.RunInTransactionAsync(async (s, ct) =>
			{
				// Typed values: an object-typed filter value makes the driver build a converting serializer by reflection, which native AOT cannot.
				var stored = (await _collection.Find(s, Builders<Event>.Filter.In(nameof(Event.EventId), list.Select(ev => ev.EventId))).ToListAsync(ct))
					.Select(ev => ev.EventId)
					.ToHashSet(); // normally empty: only a retry finds any
				var missing = list.Where(ev => !stored.Contains(ev.EventId));
				if (missing.Any())
				{
					await _collection.InsertManyAsync(s, missing, new InsertManyOptions { IsOrdered = true, }, ct);
				}
			}, cancellationToken);
		}
	}

	public async Task<Event> GetAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default)
	{
		var filter = Builders<Event>.Filter.And(Builders<Event>.Filter.Eq(StreamField, key.Stream), Builders<Event>.Filter.Eq(IdField, key.Sequence));
		return await _collection.Find(filter).FirstOrDefaultAsync(cancellationToken)
			?? throw new KeyNotFoundException($"Event ({key.Stream}, {key.Sequence}) was not found");
	}

	/// <summary>Every event in (stream, sequence) order, strictly after <paramref name="from"/> unless it is default.</summary>
	public async IAsyncEnumerable<Event> GetAllAsync((Guid Stream, Guid Sequence) from = default, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var filters = Builders<Event>.Filter;
		var filter = from == default
			? filters.Empty
			: filters.Or(
				  filters.Gt(StreamField, from.Stream)
				, filters.And(filters.Eq(StreamField, from.Stream), filters.Gt(IdField, from.Sequence))
			);
		var sort = Builders<Event>.Sort.Ascending(StreamField).Ascending(IdField);
		using var cursor = await _collection.Find(filter).Sort(sort).ToCursorAsync(cancellationToken);
		while (await cursor.MoveNextAsync(cancellationToken))
		{
			foreach (var ev in cursor.Current)
			{
				yield return ev;
			}
		}
	}

	/// <summary>One stream after a cursor: bounded by <c>_sid ==</c>, so the cursor's batches never hold another stream's documents.</summary>
	public async IAsyncEnumerable<Event> ReadStreamAsync(Guid streamId, Guid after = default, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var filters = Builders<Event>.Filter;
		var filter = after == default
			? filters.Eq(StreamField, streamId)
			: filters.And(filters.Eq(StreamField, streamId), filters.Gt(IdField, after));
		using var cursor = await _collection.Find(filter).Sort(Builders<Event>.Sort.Ascending(IdField)).ToCursorAsync(cancellationToken);
		while (await cursor.MoveNextAsync(cancellationToken))
		{
			foreach (var ev in cursor.Current)
			{
				yield return ev;
			}
		}
	}

	/// <summary>The stream's highest committed sequence: one query on the (_sid, _id) index.</summary>
	public async Task<Guid> LastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default)
	{
		var last = await _collection
			.Find(Builders<Event>.Filter.Eq(StreamField, streamId))
			.Sort(Builders<Event>.Sort.Descending(IdField))
			.Limit(1)
			.FirstOrDefaultAsync(cancellationToken);
		return last is null ? default : last.SequenceId;
	}

	public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask; // Mongo writes are durable on ack

	public void Dispose()
	{
		// The IMongoClient behind the collection is owned by DI, not this storage.
	}

	public ValueTask DisposeAsync() => default;
}
