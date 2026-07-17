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
			EventId = new Guid("C0DE0000-0000-8000-900C-0000000000B2"),
			CommandId = new Guid("C0DE0000-0000-8000-900C-0000000000B3"),
			TargetId = new Guid("C0DE0000-0000-8000-9001-0000000000B4"),
			TargetTypeId = new Guid("C0DE0000-0000-8000-9000-0000000000B5"),
			CollectionId = new Guid("C0DE0000-0000-8000-9002-0000000000B6"),
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
	public async Task Should_not_persist_JsonIgnored_DataObject()
	{
		// DataObject is the in-memory materialized object, marked [JsonIgnore]; it must not be
		// written to the durable log (mirrors the JSON-lines log). Semantic: a [JsonIgnore] member
		// is dropped by the native BSON serializer.
		var ev = new ComponentAddedEvent
		{
			EventId = new Guid("C0DE0000-0000-8000-900C-0000000000B8"),
			CommandId = new Guid("C0DE0000-0000-8000-900C-0000000000B9"),
			TargetId = new Guid("C0DE0000-0000-8000-9001-0000000000BA"),
			TargetTypeId = new Guid("C0DE0000-0000-8000-9000-0000000000BB"),
			CollectionId = new Guid("C0DE0000-0000-8000-9000-0000000000BC"),
			DataObject = new { Anything = "should not be persisted" },
		};

		var doc = ((Event)ev).ToBsonDocument(typeof(Event));
		await Assert.That(DiscriminatorLeaf(doc)).IsEqualTo("ComponentAddedEvent");
		await Assert.That(doc.Contains("DataObject")).IsFalse();

		var back = (ComponentAddedEvent)BsonSerializer.Deserialize<Event>(doc);
		await Assert.That(back.EventId).IsEqualTo(ev.EventId);
		await Assert.That(back.TargetId).IsEqualTo(ev.TargetId);
	}

	[Test]
	public async Task Should_not_write_a_null_field_at_all()
	{
		// Data is a plain (non-JsonIgnore'd) property; when null it must not be written as an
		// explicit "Data": null. Without the global IgnoreIfNullConvention, the document would
		// still carry it for every single event, just to record the absence of a value.
		var ev = new ComponentAddedEvent
		{
			EventId = new Guid("C0DE0000-0000-8000-900C-0000000000BE"),
			CommandId = new Guid("C0DE0000-0000-8000-900C-0000000000BF"),
			TargetId = new Guid("C0DE0000-0000-8000-9001-0000000000C0"),
			TargetTypeId = new Guid("C0DE0000-0000-8000-9000-0000000000C1"),
			CollectionId = new Guid("C0DE0000-0000-8000-9000-0000000000C2"),
			Data = null,
		};

		var doc = ((Event)ev).ToBsonDocument(typeof(Event));
		await Assert.That(doc.Contains("Data")).IsFalse();

		var back = (ComponentAddedEvent)BsonSerializer.Deserialize<Event>(doc);
		await Assert.That(back.Data).IsNull();
	}

	[Test]
	public async Task Should_not_duplicate_LinkId_SourceId_TargetId_inside_Data()
	{
		// LinkAddedEvent already carries LinkId/SourceId/TargetId as its own explicit fields (see
		// AddLinkCommand's remarks on why) — Data should only ever add a concrete subtype's own
		// extra properties (a primitive link like HierarchyLink has none, so Data should come back
		// essentially empty: just its own discriminator, nothing from the Link base).
		var linkId = new Guid("C0DE0000-0000-8000-9003-0000000000C3");
		var sourceId = new Guid("C0DE0000-0000-8000-9001-0000000000C4");
		var targetId = new Guid("C0DE0000-0000-8000-9001-0000000000C5");
		var ev = new LinkAddedEvent
		{
			EventId = new Guid("C0DE0000-0000-8000-900C-0000000000C6"),
			CommandId = new Guid("C0DE0000-0000-8000-900C-0000000000C7"),
			LinkTypeId = new Guid("C0DE0000-0000-8000-9000-0000000000C8"),
			LinkId = linkId,
			SourceId = sourceId,
			TargetId = targetId,
			Data = new HierarchyLink { LinkId = linkId, SourceId = sourceId, TargetId = targetId },
		};

		var doc = ((Event)ev).ToBsonDocument(typeof(Event));
		await Assert.That(DiscriminatorLeaf(doc)).IsEqualTo("LinkAddedEvent");
		// The event's own explicit fields are untouched.
		await Assert.That(doc["LinkId"].AsGuid).IsEqualTo(linkId);
		await Assert.That(doc["SourceId"].AsGuid).IsEqualTo(sourceId);
		await Assert.That(doc["TargetId"].AsGuid).IsEqualTo(targetId);
		// But the nested Data blob does not repeat them.
		var data = doc["Data"].AsBsonDocument;
		await Assert.That(data.Contains("LinkId")).IsFalse();
		await Assert.That(data.Contains("SourceId")).IsFalse();
		await Assert.That(data.Contains("TargetId")).IsFalse();

		var back = (LinkAddedEvent)BsonSerializer.Deserialize<Event>(doc);
		await Assert.That(back.LinkId).IsEqualTo(linkId);
		await Assert.That(back.SourceId).IsEqualTo(sourceId);
		await Assert.That(back.TargetId).IsEqualTo(targetId);
	}

	[Test]
	public async Task Should_use_id_field_for_event_key()
	{
		// The whole point of mapping EventId -> _id is that a document's natural key is the
		// event id, so dedup / idempotent inserts and ordered replay work.
		var ev = new ObjectPropertyChangedEvent
		{
			EventId = new Guid("C0DE0000-0000-8000-900C-0000000000BD"),
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
