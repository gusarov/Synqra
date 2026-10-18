using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// Upgrade for event logs written before <c>_id</c> became the commit sequence (docs/model.md §9): those documents
/// were keyed by EventId and carry no <c>EventId</c> field, so it is backfilled from <c>_id</c> (which is what it was).
/// Then the indexes the sequenced log relies on: EventId unique (the identity; a re-append is a no-op) and
/// (_sid, _id) for a stream's tail. Idempotent; invoked only by <see cref="SynqraMongoUpgradeService"/> under its
/// single-node claim.
/// </summary>
internal sealed class EventSequenceUpgrade : ISynqraMongoUpgrade
{
	const string EventIdField = EventLogIndexes.EventIdField;

	public string Id => "event-sequence/1";

	public SynqraUpgradeScope Scope => SynqraUpgradeScope.Events;

	public async Task RunAsync(IMongoDatabase eventsDatabase, string eventsCollectionName, IMongoDatabase? projectionDatabase, ILogger logger, CancellationToken cancellationToken)
	{
		var events = eventsDatabase.GetCollection<BsonDocument>(eventsCollectionName);
		var missing = Builders<BsonDocument>.Filter.Exists(EventIdField, false);
		var backfill = Builders<BsonDocument>.Update.Pipeline(PipelineDefinition<BsonDocument, BsonDocument>.Create(new BsonDocument("$set", new BsonDocument(EventIdField, "$_id"))));
		var backfilled = await events.UpdateManyAsync(missing, backfill, cancellationToken: cancellationToken);
		if (backfilled.ModifiedCount > 0)
		{
			logger.LogWarning("Event log upgrade: {Count} legacy event document(s) got EventId = _id.", backfilled.ModifiedCount);
		}
		await new EventLogIndexes().EnsureAsync(eventsDatabase, eventsCollectionName, cancellationToken);
		logger.LogInformation("Event log upgrade: ensured sequence indexes on '{Database}.{Collection}'.", eventsDatabase.DatabaseNamespace.DatabaseName, eventsCollectionName);
	}
}
