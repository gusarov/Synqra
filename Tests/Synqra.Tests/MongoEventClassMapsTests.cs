using System.Reflection;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization;
using Synqra.AppendStorage.MongoDb;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// The driver learns a discriminator when it first serializes a type. A process that only reads the log
/// (a REST host after a redeploy) never does, so every event and every command (they ride inside
/// CommandCreatedEvent.Data) must be known up front: "Unknown discriminator value 'BatchCommand'" on the preview.
/// </summary>
public class MongoEventClassMapsTests
{
	[Test]
	public async Task Should_know_every_derived_event_and_command_before_any_is_serialized()
	{
		MongoEventClassMaps.Register();
		foreach (var root in new[] { typeof(Event), typeof(Command) })
		{
			foreach (var derived in root.GetCustomAttributes<JsonDerivedTypeAttribute>())
			{
				var actual = BsonSerializer.LookupActualType(root, (string)derived.TypeDiscriminator!);
				await Assert.That(actual).IsEqualTo(derived.DerivedType);
			}
		}
	}
}
