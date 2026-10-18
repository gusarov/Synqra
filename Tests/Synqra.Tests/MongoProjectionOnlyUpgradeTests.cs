using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Synqra.AppendStorage.MongoDb;
using Synqra.Projection.MongoDb;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// A Mongo projection over a non-Mongo event log (no Mongo event storage registered) still gets its
/// projection upgrades from <see cref="SynqraMongoUpgradeService"/>, claimed in the projection database.
/// </summary>
public class MongoProjectionOnlyUpgradeTests : BaseTest
{
	IMongoDatabase OpenFreshDatabase()
	{
		return new MongoClient(new MongoUrl(_connectionString)).GetDatabase("projonly_" + Guid.NewGuid().ToString("N"));
	}

	SynqraMongoUpgradeService Runner(IMongoDatabase db, bool withEventStorage)
	{
		var services = new ServiceCollection();
		services.AddSingleton(db);
		if (withEventStorage)
		{
			services.AddMongoEventStorage();
		}
		services.AddMongoProjection();
		return services.BuildServiceProvider().GetServices<IHostedService>().OfType<SynqraMongoUpgradeService>().Single();
	}

	[Test]
	public async Task Should_create_components_and_text_indexes_for_a_projection_without_mongo_event_storage()
	{
		var db = OpenFreshDatabase();
		try
		{
			await Runner(db, withEventStorage: false).StartAsync(CancellationToken.None);

			var indexes = (await db.GetCollection<BsonDocument>("Components").Indexes.List().ToListAsync()).Select(i => i["name"].AsString).ToList();
			await Assert.That(indexes).Contains("components_text");
			await Assert.That(indexes.Count(n => n.StartsWith("_sid_1"))).IsGreaterThanOrEqualTo(4);

			var claims = await db.GetCollection<BsonDocument>("_synqra_upgrades").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
			await Assert.That(claims.Select(c => c["_id"].AsString).Order().ToList()).IsEquivalentTo(["components-indexes/3", "components-text/1"]);
			await Assert.That(claims.All(c => c.Contains("completedAt"))).IsTrue();

			// Second boot: completed claims short-circuit.
			await Runner(db, withEventStorage: false).StartAsync(CancellationToken.None);
			await Assert.That(await db.GetCollection<BsonDocument>("_synqra_upgrades").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).IsEqualTo(2L);
		}
		finally
		{
			await db.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
		}
	}

	[Test]
	public async Task Should_run_each_upgrade_once_when_the_key_has_both_event_storage_and_projection()
	{
		var db = OpenFreshDatabase();
		try
		{
			await Runner(db, withEventStorage: true).StartAsync(CancellationToken.None);

			var claims = await db.GetCollection<BsonDocument>("_synqra_upgrades").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
			await Assert.That(claims.Select(c => c["_id"].AsString).Distinct().Count()).IsEqualTo(claims.Count);
			await Assert.That(claims.Count(c => c["_id"].AsString.StartsWith("components-"))).IsEqualTo(2);
			await Assert.That(claims.All(c => c.Contains("completedAt"))).IsTrue();
		}
		finally
		{
			await db.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
		}
	}
}
