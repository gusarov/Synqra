using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Synqra.AppendStorage.MongoDb;
using Synqra.Tests.SampleModels;

namespace Synqra.Tests;

/// <summary>A model that dropped a property still reads the documents written while it had one: the extra element is skipped, never a failure.</summary>
public class MongoDroppedPropertyTests
{
	[Test]
	public async Task Should_read_a_document_holding_a_property_the_model_dropped()
	{
		MongoEventClassMaps.Register();
		var document = new BsonDocument { { "_t", nameof(TestKeyedComponent) }, { "Key", "1270" }, { "Owner", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) }, { "CreatedAt", BsonDateTime.Create(DateTime.UtcNow) }, };

		var component = BsonSerializer.Deserialize<TestKeyedComponent>(document);

		await Assert.That(component.Key).IsEqualTo("1270");
	}
}
