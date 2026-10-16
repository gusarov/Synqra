using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>The event log's indexes (docs/model.md §9): EventId unique (the identity; a re-append is a no-op) and (_sid, _id) for a stream's tail. Idempotent.</summary>
internal sealed class EventLogIndexes
{
	public const string EventIdField = "EventId";
	public const string StreamIdField = "_sid";

	public Task EnsureAsync(IMongoDatabase database, string collectionName, CancellationToken cancellationToken = default)
		=> database.GetCollection<BsonDocument>(collectionName).Indexes.CreateManyAsync(
			[
				new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending(EventIdField), new CreateIndexOptions { Unique = true, Name = "EventId_unique" }),
				new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending(StreamIdField).Ascending("_id"), new CreateIndexOptions { Name = "sid_id" }),
			]
			, cancellationToken);
}
