using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.Extensions;

namespace Synqra.Utils.Tests;

[Property("CI", "false")]
internal class MongoDbUuidOrdering : BaseTest
{
	public MongoDbUuidOrdering()
	{
		BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
		// BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.JavaLegacy));
		// BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.CSharpLegacy));
	}

	public class UuidDocument
	{
		public Guid Id { get; set; }
		public int Order { get; set; }
	}

	[Test]
	public async Task Should_preserve_uuid7_order_in_mongo()
	{
		var mongoClient = new MongoClient("mongodb://localhost:27017");
		var database = mongoClient.GetDatabase("_synqra_integration");
		var uuids = database.GetCollection<UuidDocument>("uuids_c");
		uuids.DeleteMany(x => true);

		database.CreateCollection("uuids_c", new CreateCollectionOptions<UuidDocument>() { ClusteredIndex = new ClusteredIndexOptions<UuidDocument>(), });

		var rnd = new Random();
		for (int i = 0; i < 1024; i++)
		{
			if (rnd.Next(30) == 0)
			{
				Thread.Sleep(10);
			}
			uuids.InsertOne(new UuidDocument
			{
				Id = GuidExtensions.CreateVersion7(),
				Order = i,
			});
		}
		var all = uuids.Find(x => true).ToList();
		for (int i = 0; i < 1024; i++)
		{
			// Assert.That(all[i].Order, Is.EqualTo(i), $"Order mismatch at {i}");
			await Assert.That(all[i].Order).IsEqualTo(i);
		}
	}
}
