using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// One-time, idempotent upgrade for event logs written before <see cref="Event.StreamId"/> was
/// persisted as <c>_sid</c> (see <see cref="MongoEventClassMaps"/>): those documents carry no
/// stream column at all, so the per-stream <c>AppendStorageEventLog</c> cannot isolate them and
/// every stream sees them. This backfills <c>_sid</c> onto every legacy event from the data the
/// log (and its projection) already contains — no operator input, safe to run on every startup:
/// <list type="number">
/// <item><b>CommandCreatedEvent join:</b> a <c>CommandCreatedEvent</c>'s embedded command
/// (<c>Data.StreamId</c>) names its stream outright — that resolves the CommandCreatedEvent
/// itself and, joined via <c>CommandId</c>, every other event the same command produced.</item>
/// <item><b>Projection fallback:</b> events with no surviving CommandCreatedEvent (e.g. an early
/// seed path that never logged commands) are resolved through their <c>TargetId</c>: the
/// materialized document for that object in the projection database already carries <c>_sid</c>
/// (MongoProjection has stamped it since stream scoping landed).</item>
/// </list>
/// Anything still unresolved after both passes is left untouched and reported — an event whose
/// stream genuinely cannot be derived should be looked at by a human, not guessed at.
/// </summary>
public static class MongoEventSidBackfill
{
	const string StreamIdField = "_sid";

	/// <summary>
	/// Backfills <c>_sid</c> on every event document that lacks one. Idempotent and cheap when
	/// there is nothing to do (a single indexed-by-nothing count on a small collection; the
	/// full scan work only happens when legacy documents actually exist).
	/// </summary>
	/// <param name="eventsDatabase">Database holding the event log collection.</param>
	/// <param name="projectionDatabase">Database holding the materialized projection collections
	/// (may be the same database). Used only for the fallback pass.</param>
	/// <param name="eventsCollectionName">Event log collection name (default "Event").</param>
	/// <returns>(backfilled, unresolved) counts.</returns>
	public static async Task<(long Backfilled, long Unresolved)> BackfillAsync(
		  IMongoDatabase eventsDatabase
		, IMongoDatabase projectionDatabase
		, string eventsCollectionName = "Event"
		, ILogger? logger = null
		, CancellationToken cancellationToken = default
		)
	{
		var events = eventsDatabase.GetCollection<BsonDocument>(eventsCollectionName);
		var missingFilter = Builders<BsonDocument>.Filter.Exists(StreamIdField, false);

		var missingCount = await events.CountDocumentsAsync(missingFilter, cancellationToken: cancellationToken);
		if (missingCount == 0)
		{
			return (0, 0);
		}
		logger?.LogWarning("Event log upgrade: {Count} event document(s) lack {Field} — backfilling from CommandCreatedEvent joins and the projection.", missingCount, StreamIdField);

		// Pass 1 source: CommandId -> StreamId from every CommandCreatedEvent whose embedded
		// command names its stream. Read once up front — the log is append-only, so this map
		// cannot go stale under us, and legacy logs are small enough to hold it in memory.
		var commandStreams = new Dictionary<Guid, Guid>();
		using (var cursor = await events
			.Find(Builders<BsonDocument>.Filter.Eq("_t", "CommandCreatedEvent"))
			.ToCursorAsync(cancellationToken))
		{
			while (await cursor.MoveNextAsync(cancellationToken))
			{
				foreach (var doc in cursor.Current)
				{
					if (doc.TryGetValue("Data", out var data) && data is BsonDocument cmd
						&& cmd.TryGetValue("StreamId", out var sidValue) && sidValue is BsonBinaryData
						&& doc.TryGetValue("CommandId", out var cidValue) && cidValue is BsonBinaryData)
					{
						commandStreams[cidValue.AsGuid] = sidValue.AsGuid;
					}
				}
			}
		}

		// Pass 2 source is resolved lazily per TargetId (with a cache): the projection database's
		// collections are enumerated once, and each is probed by _id. The projection has stamped
		// _sid on every materialized document since stream scoping landed, so any object a legacy
		// event created/updated resolves here even when its command was never logged.
		var projectionCollections = new List<IMongoCollection<BsonDocument>>();
		using (var names = await projectionDatabase.ListCollectionNamesAsync(cancellationToken: cancellationToken))
		{
			foreach (var name in await names.ToListAsync(cancellationToken))
			{
				if (!name.StartsWith("system.", StringComparison.Ordinal))
				{
					projectionCollections.Add(projectionDatabase.GetCollection<BsonDocument>(name));
				}
			}
		}
		var targetStreams = new Dictionary<Guid, Guid?>();
		async Task<Guid?> ResolveByTargetAsync(Guid targetId)
		{
			if (targetStreams.TryGetValue(targetId, out var cached))
			{
				return cached;
			}
			Guid? resolved = null;
			foreach (var collection in projectionCollections)
			{
				// Explicit BsonBinaryData, not a raw Guid: a raw Guid in a filter resolves the
				// driver's ambient GuidSerializer, which throws when no process-wide
				// representation was ever configured — this helper must not depend on any
				// class-map/serializer registration having happened first.
				var doc = await collection
					.Find(Builders<BsonDocument>.Filter.Eq("_id", new BsonBinaryData(targetId, GuidRepresentation.Standard)))
					.Project(Builders<BsonDocument>.Projection.Include(StreamIdField))
					.FirstOrDefaultAsync(cancellationToken);
				if (doc is not null && doc.TryGetValue(StreamIdField, out var sid) && sid is BsonBinaryData)
				{
					resolved = sid.AsGuid;
					break;
				}
			}
			targetStreams[targetId] = resolved;
			return resolved;
		}

		long backfilled = 0, unresolved = 0;
		var updates = new List<WriteModel<BsonDocument>>();
		using (var cursor = await events.Find(missingFilter).ToCursorAsync(cancellationToken))
		{
			while (await cursor.MoveNextAsync(cancellationToken))
			{
				foreach (var doc in cursor.Current)
				{
					Guid? sid = null;
					if (doc.TryGetValue("CommandId", out var cidValue) && cidValue is BsonBinaryData
						&& commandStreams.TryGetValue(cidValue.AsGuid, out var fromCommand))
					{
						sid = fromCommand;
					}
					else if (doc.TryGetValue("TargetId", out var tidValue) && tidValue is BsonBinaryData)
					{
						sid = await ResolveByTargetAsync(tidValue.AsGuid);
					}

					if (sid is Guid resolvedSid)
					{
						updates.Add(new UpdateOneModel<BsonDocument>(
							  Builders<BsonDocument>.Filter.Eq("_id", doc["_id"])
							, Builders<BsonDocument>.Update.Set(StreamIdField, new BsonBinaryData(resolvedSid, GuidRepresentation.Standard))));
						backfilled++;
					}
					else
					{
						unresolved++;
						logger?.LogWarning("Event log upgrade: cannot derive a stream for event {EventId} ({Type}) — leaving it without {Field}.", doc.GetValue("_id", BsonNull.Value), doc.GetValue("_t", BsonNull.Value), StreamIdField);
					}
				}
			}
		}
		if (updates.Count > 0)
		{
			await events.BulkWriteAsync(updates, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
		}
		logger?.LogWarning("Event log upgrade: backfilled {Backfilled} event(s); {Unresolved} left unresolved.", backfilled, unresolved);
		return (backfilled, unresolved);
	}
}
