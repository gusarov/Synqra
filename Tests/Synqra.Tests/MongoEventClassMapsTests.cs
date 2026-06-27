using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Synqra.AppendStorage.MongoDb;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// In-process validation of the native BSON class maps for the Event hierarchy. These
/// don't need a Mongo server — they exercise the same serializer the driver uses
/// (<see cref="BsonSerializer"/>) to prove the regular core events round-trip to a
/// <see cref="BsonDocument"/> and back, with the <c>_t</c> discriminator and <c>_id</c>
/// mapping the durable Mongo log relies on.
/// </summary>
[NotInParallel]
public class MongoEventClassMapsTests
{
	[Before(Test)]
	public void Setup()
	{
		MongoEventClassMaps.Register();
	}

	// MongoDB's hierarchical discriminator stores `_t` as either a scalar string or an
	// array (type chain). The concrete type is the scalar value or the last array element.
	static string DiscriminatorLeaf(BsonDocument doc)
	{
		var t = doc["_t"];
		return t.IsBsonArray ? t.AsBsonArray[^1].AsString : t.AsString;
	}

	[Test]
	public async Task Should_round_trip_ObjectPropertyChangedEvent_via_native_bson()
	{
		var ev = new ObjectPropertyChangedEvent
		{
			EventId = Guid.Parse("00000020-0002-8000-8000-0000000000b2"),
			CommandId = Guid.Parse("00000020-0003-8000-8000-0000000000b3"),
			TargetId = Guid.Parse("00000020-0004-8000-8000-0000000000b4"),
			TargetTypeId = Guid.Parse("00000020-0005-8000-8000-0000000000b5"),
			CollectionId = Guid.Parse("00000020-0006-8000-8000-0000000000b6"),
			PropertyName = "Name",
			NewValue = "Alice",
		};

		// Serialize through the polymorphic base — this is what IMongoCollection<Event> does.
		var doc = ((Event)ev).ToBsonDocument(typeof(Event));

		// Discriminator + id mapping the Mongo log depends on. MongoDB uses a hierarchical
		// discriminator (the native idiom) — `_t` is the type chain, concrete type last.
		await Assert.That(DiscriminatorLeaf(doc)).IsEqualTo("ObjectPropertyChangedEvent");
		await Assert.That(doc["_id"].AsGuid).IsEqualTo(ev.EventId);
		// StreamId is intentionally unmapped (out-of-band routing concern).
		await Assert.That(doc.Contains("StreamId")).IsFalse();

		var back = (ObjectPropertyChangedEvent)BsonSerializer.Deserialize<Event>(doc);
		await Assert.That(back.EventId).IsEqualTo(ev.EventId);
		await Assert.That(back.CommandId).IsEqualTo(ev.CommandId);
		await Assert.That(back.TargetId).IsEqualTo(ev.TargetId);
		await Assert.That(back.PropertyName).IsEqualTo("Name");
		await Assert.That((string?)back.NewValue).IsEqualTo("Alice");
	}

	[Test]
	public async Task Should_not_persist_JsonIgnored_DataObject_of_ObjectCreatedEvent()
	{
		// DataObject is the in-memory materialized object, marked [JsonIgnore]; it must not
		// be written to the durable log (mirrors the JSON-lines log).
		var ev = new ObjectCreatedEvent
		{
			EventId = Guid.Parse("00000020-0008-8000-8000-0000000000b8"),
			CommandId = Guid.Parse("00000020-0009-8000-8000-0000000000b9"),
			TargetId = Guid.Parse("00000020-000a-8000-8000-0000000000ba"),
			TargetTypeId = Guid.Parse("00000020-000b-8000-8000-0000000000bb"),
			CollectionId = Guid.Parse("00000020-000c-8000-8000-0000000000bc"),
			DataObject = new { Anything = "should not be persisted" },
		};

		var doc = ((Event)ev).ToBsonDocument(typeof(Event));
		await Assert.That(DiscriminatorLeaf(doc)).IsEqualTo("ObjectCreatedEvent");
		await Assert.That(doc.Contains("DataObject")).IsFalse();

		var back = (ObjectCreatedEvent)BsonSerializer.Deserialize<Event>(doc);
		await Assert.That(back.EventId).IsEqualTo(ev.EventId);
		await Assert.That(back.TargetId).IsEqualTo(ev.TargetId);
	}

	[Test]
	public async Task Should_not_write_a_null_field_at_all()
	{
		// Data is a plain (non-JsonIgnore'd) property, never populated by the current
		// command-handling path for ObjectCreatedEvent — it's null on every real event of this
		// kind. Without the global IgnoreIfNullConvention, the document would still carry an
		// explicit "Data": null for every single event, just to record the absence of a value.
		var ev = new ObjectCreatedEvent
		{
			EventId = Guid.Parse("00000020-000e-8000-8000-0000000000be"),
			CommandId = Guid.Parse("00000020-000f-8000-8000-0000000000bf"),
			TargetId = Guid.Parse("00000020-0010-8000-8000-0000000000c0"),
			TargetTypeId = Guid.Parse("00000020-0011-8000-8000-0000000000c1"),
			CollectionId = Guid.Parse("00000020-0012-8000-8000-0000000000c2"),
			Data = null,
		};

		var doc = ((Event)ev).ToBsonDocument(typeof(Event));
		await Assert.That(doc.Contains("Data")).IsFalse();

		var back = (ObjectCreatedEvent)BsonSerializer.Deserialize<Event>(doc);
		await Assert.That(back.Data).IsNull();
	}

	[Test]
	public async Task Should_use_id_field_for_event_key()
	{
		// The whole point of mapping EventId -> _id is that a document's natural key is the
		// event id, so dedup / idempotent inserts and ordered replay work.
		var ev = new ObjectPropertyChangedEvent
		{
			EventId = Guid.Parse("00000020-000d-8000-8000-0000000000bd"),
			CommandId = Guid.Empty,
			TargetId = Guid.Empty,
			TargetTypeId = Guid.Empty,
			CollectionId = Guid.Empty,
			PropertyName = "Name",
			NewValue = null,
		};
		var doc = ((Event)ev).ToBsonDocument(typeof(Event));
		await Assert.That(doc.Contains("_id")).IsTrue();
		await Assert.That(doc.Contains("EventId")).IsFalse(); // mapped to _id, not duplicated
		await Assert.That(doc["_id"].AsGuid).IsEqualTo(ev.EventId);
	}
}
