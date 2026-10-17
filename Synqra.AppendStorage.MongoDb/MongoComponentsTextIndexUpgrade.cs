using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// Creates the text index of the projection's shared <c>Components</c> collection: <c>(_sid, $** text)</c>, so a stream's
/// roots and facets are searchable by every string they hold (their <c>_t</c> type name included) and a search always
/// names its stream. Language <c>none</c>: no stemming or stop words, so words match whole in any language. The language
/// override points at a field nothing writes, so a model's own <c>language</c> property is plain data.
/// Idempotent; run once per database by <see cref="SynqraMongoUpgradeService"/>.
/// </summary>
internal sealed class ComponentsTextIndexUpgrade : ISynqraMongoUpgrade
{
	const string ComponentsCollectionName = "Components";
	const string StreamIdField = "_sid";

	public string Id => "components-text/1";

	public async Task RunAsync(IMongoDatabase eventsDatabase, string eventsCollectionName, IMongoDatabase? projectionDatabase, ILogger logger, CancellationToken cancellationToken)
	{
		if (projectionDatabase is not null)
		{
			var components = projectionDatabase.GetCollection<BsonDocument>(ComponentsCollectionName);
			await components.Indexes.CreateOneAsync(
				new CreateIndexModel<BsonDocument>(
					Builders<BsonDocument>.IndexKeys.Ascending(StreamIdField).Text("$**"),
					new CreateIndexOptions { Name = "components_text", DefaultLanguage = "none", LanguageOverride = "_synqra_language" }),
				options: null, cancellationToken);
			logger.LogInformation("Projection upgrade: ensured the Components text index on '{Database}'.", projectionDatabase.DatabaseNamespace.DatabaseName);
		}
	}
}
