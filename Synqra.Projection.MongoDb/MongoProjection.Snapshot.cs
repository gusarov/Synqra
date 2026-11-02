using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.Projection.MongoDb;

// A stream snapshot cut from the materialized documents (plans/snapshot.md §4): a cold client loads the stream's state, not its history.
public sealed partial class MongoProjection : IStreamSnapshotSource
{
	/// <summary>Every document of the stream that is not its entity's root: the facets and the links.</summary>
	FilterDefinition<BsonDocument> HangingOnlyFilter => new BsonDocument("$expr", new BsonDocument("$ne", new BsonArray { "$_id", "$" + EntityIdField }));

	/// <summary>
	/// The stream's state at its watermark. A write appends to the log and commits its documents under the stream's gate, so
	/// under that gate the documents are exactly the state at the watermark. The gate is held only for an instant: the
	/// watermark is read, and a snapshot session (which fixes its point in time at its first read) issues that first read;
	/// the rest of the scan runs after the gate opens and sees none of the writes made meanwhile. Writes made through another
	/// path (the replication endpoint applies a peer's event, then appends it) are kept out by the endpoint, which cuts
	/// under the same gate its own writes take. Null for a stream with no entity.
	/// </summary>
	public Task<SnapshotCut?> CutAsync(Guid streamId, CancellationToken cancellationToken = default)
		=> GateOf(streamId).RunAsync(async () =>
		{
			var watermark = _eventStorage is IEventWatermarks marks ? await marks.ReadWatermarkAsync(streamId, cancellationToken) : default;
			var session = await _database.Client.StartSessionAsync(new ClientSessionOptions { Snapshot = true, }, cancellationToken);
			SnapshotCut? cut = null;
			try
			{
				var stream = Builders<BsonDocument>.Filter.Eq(StreamIdField, streamId);
				var roots = await Components.FindAsync(
					  session
					, Builders<BsonDocument>.Filter.And(stream, RootsOnlyFilter)
					, new FindOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Ascending(EntityIdField), } // a root's _eid is its _id: the (_sid, _eid) index serves the order
					, cancellationToken
					);
				// The first read, inside the gate: it fixes the session's point in time.
				var first = await roots.MoveNextAsync(cancellationToken) ? roots.Current.ToList() : [];
				if (first.Count > 0)
				{
					var hanging = Builders<BsonDocument>.Filter.And(stream, HangingOnlyFilter);
					cut = new SnapshotCut(watermark, ReadRecordsAsync(session, first, roots, hanging, cancellationToken), () =>
					{
						roots.Dispose();
						session.Dispose();
						return ValueTask.CompletedTask;
					});
				}
				else
				{
					roots.Dispose();
				}
			}
			finally
			{
				if (cut is null)
				{
					session.Dispose();
				}
			}
			return cut;
		});

	/// <summary>Roots in id order, then facets and links grouped by owner: a record never arrives before the entity it hangs on.</summary>
	async IAsyncEnumerable<SnapshotRecord> ReadRecordsAsync(IClientSessionHandle session, List<BsonDocument> first, IAsyncCursor<BsonDocument> roots, FilterDefinition<BsonDocument> hanging, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		foreach (var doc in first)
		{
			yield return ReadRecord(doc);
		}
		while (await roots.MoveNextAsync(cancellationToken))
		{
			foreach (var doc in roots.Current)
			{
				yield return ReadRecord(doc);
			}
		}
		using var rest = await Components.FindAsync(
			  session
			, hanging
			, new FindOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Ascending(EntityIdField), } // by owner, from the (_sid, _eid) index: no in-memory sort
			, cancellationToken
			);
		while (await rest.MoveNextAsync(cancellationToken))
		{
			foreach (var doc in rest.Current)
			{
				yield return ReadRecord(doc);
			}
		}
	}

	SnapshotRecord ReadRecord(BsonDocument doc)
	{
		var id = doc["_id"].AsGuid;
		var owner = doc[EntityIdField].AsGuid;
		var version = owner == id && doc.TryGetValue(LastEventIdField, out var lev) && lev is BsonBinaryData binary ? binary.ToGuid(GuidRepresentation.Standard) : (Guid?)null;
		return new SnapshotRecord
		{
			ComponentId = id,
			Owner = owner == id ? null : owner,
			Version = version,
			Data = FromDocument(doc),
		};
	}
}
