using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// Creates the query indexes of the projection's shared <c>Components</c> collection (the one
/// collection every root and facet lives in — see <c>MongoProjection</c>): <c>(_sid, _t)</c> serves
/// type-scoped enumeration and <c>Count</c>, <c>(_sid, _t, _id)</c> pages a type in id order, <c>(_sid, _t, _eid)</c> finds the owners of a facet type, <c>(_sid, _eid)</c> serves facet load, component
/// addressing and entity delete, <c>(_sid, TargetId)</c> on link documents serves the reverse side of a link. Idempotent (CreateMany on existing identical indexes is a no-op);
/// invoked only by <see cref="SynqraMongoUpgradeService"/> under its single-node claim, so index
/// builds never race across nodes and never run on a hot path.
/// </summary>
internal sealed class ComponentsIndexesUpgrade : ISynqraMongoUpgrade
{
	const string ComponentsCollectionName = "Components";
	const string StreamIdField = "_sid";
	const string DiscriminatorField = "_t";
	const string EntityIdField = "_eid";
	const string LinkTargetField = "TargetId";

	public string Id => "components-indexes/3"; // 2: (_sid, _t, _id) pages a type in id order, (_sid, _t, _eid) finds the owners of a facet type; 3: (_sid, TargetId) for links

	public SynqraUpgradeScope Scope => SynqraUpgradeScope.Projection;

	public async Task RunAsync(IMongoDatabase eventsDatabase, string eventsCollectionName, IMongoDatabase? projectionDatabase, ILogger logger, CancellationToken cancellationToken)
	{
		if (projectionDatabase is null)
		{
			return; // event log only — nothing materialized here to index
		}
		var components = projectionDatabase.GetCollection<BsonDocument>(ComponentsCollectionName);
		await components.Indexes.CreateManyAsync(
			[
				new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending(StreamIdField).Ascending(DiscriminatorField)),
				new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending(StreamIdField).Ascending(EntityIdField)),
				new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending(StreamIdField).Ascending(DiscriminatorField).Ascending("_id")),
				new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending(StreamIdField).Ascending(DiscriminatorField).Ascending(EntityIdField)),
				new CreateIndexModel<BsonDocument>(
					Builders<BsonDocument>.IndexKeys.Ascending(StreamIdField).Ascending(LinkTargetField)
					, new CreateIndexOptions<BsonDocument> { PartialFilterExpression = Builders<BsonDocument>.Filter.Exists(LinkTargetField) }
					),
			]
			, cancellationToken);
		logger.LogInformation("Projection upgrade: ensured Components indexes on '{Database}'.", projectionDatabase.DatabaseNamespace.DatabaseName);
	}
}
