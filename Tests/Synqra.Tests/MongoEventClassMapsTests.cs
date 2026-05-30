using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Synqra.AppendStorage.MongoDb;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// In-process validation of the native BSON class maps for the Event hierarchy.
/// These don't need a Mongo server — they exercise the same serializer the driver
/// uses (<see cref="BsonSerializer"/>) to prove polymorphic events round-trip to a
/// <see cref="BsonDocument"/> and back, with the <c>_t</c> discriminator and
/// <c>_id</c> mapping the durable Mongo log relies on.
/// </summary>
[NotInParallel]
public class MongoEventClassMapsTests
{
    [Before(Test)]
    public void Setup() => MongoEventClassMaps.Register();

    // MongoDB's hierarchical discriminator stores `_t` as either a scalar string or
    // an array (type chain). The concrete type is the scalar value or the last array
    // element either way.
    static string DiscriminatorLeaf(BsonDocument doc)
    {
        var t = doc["_t"];
        return t.IsBsonArray ? t.AsBsonArray[^1].AsString : t.AsString;
    }

    [Test]
    public async Task Should_round_trip_WireAddedEvent_via_native_bson()
    {
        var wireId = Guid.Parse("00000020-0001-7000-8000-0000000000b1");
        var ev = new WireAddedEvent
        {
            EventId = Guid.Parse("00000020-0002-7000-8000-0000000000b2"),
            CommandId = Guid.Parse("00000020-0003-7000-8000-0000000000b3"),
            WireId = wireId,
            SourceContainerId = Guid.Parse("00000020-0004-7000-8000-0000000000b4"),
            SourceComponentTypeId = Guid.Parse("00000020-0005-7000-8000-0000000000b5"),
            SourcePortName = "out",
            TargetContainerId = Guid.Parse("00000020-0006-7000-8000-0000000000b6"),
            TargetComponentTypeId = Guid.Parse("00000020-0007-7000-8000-0000000000b7"),
            TargetPortName = "in",
            Type = (int)PortType.Event,
        };

        // Serialize through the polymorphic base — this is what IMongoCollection<Event> does.
        var doc = ((Event)ev).ToBsonDocument(typeof(Event));

        // Discriminator + id mapping the Mongo log depends on. MongoDB uses a
        // hierarchical discriminator (the native idiom) — `_t` is the type chain,
        // with the concrete type last. A query `{ _t: "WireAddedEvent" }` matches it.
        await Assert.That(DiscriminatorLeaf(doc)).IsEqualTo("WireAddedEvent");
        await Assert.That(doc["_id"].AsGuid).IsEqualTo(ev.EventId);
        // StreamId is intentionally unmapped (out-of-band routing concern).
        await Assert.That(doc.Contains("StreamId")).IsFalse();

        var back = (WireAddedEvent)BsonSerializer.Deserialize<Event>(doc);
        await Assert.That(back.EventId).IsEqualTo(ev.EventId);
        await Assert.That(back.CommandId).IsEqualTo(ev.CommandId);
        await Assert.That(back.WireId).IsEqualTo(wireId);
        await Assert.That(back.SourceContainerId).IsEqualTo(ev.SourceContainerId);
        await Assert.That(back.SourceComponentTypeId).IsEqualTo(ev.SourceComponentTypeId);
        await Assert.That(back.SourcePortName).IsEqualTo("out");
        await Assert.That(back.TargetContainerId).IsEqualTo(ev.TargetContainerId);
        await Assert.That(back.TargetComponentTypeId).IsEqualTo(ev.TargetComponentTypeId);
        await Assert.That(back.TargetPortName).IsEqualTo("in");
        await Assert.That(back.Type).IsEqualTo((int)PortType.Event);
    }

    [Test]
    public async Task Should_round_trip_WireDeletedEvent_via_native_bson()
    {
        var ev = new WireDeletedEvent
        {
            EventId = Guid.Parse("00000020-0008-7000-8000-0000000000b8"),
            CommandId = Guid.Parse("00000020-0009-7000-8000-0000000000b9"),
            WireId = Guid.Parse("00000020-000a-7000-8000-0000000000ba"),
        };

        var doc = ((Event)ev).ToBsonDocument(typeof(Event));
        await Assert.That(DiscriminatorLeaf(doc)).IsEqualTo("WireDeletedEvent");

        var back = (WireDeletedEvent)BsonSerializer.Deserialize<Event>(doc);
        await Assert.That(back.EventId).IsEqualTo(ev.EventId);
        await Assert.That(back.WireId).IsEqualTo(ev.WireId);
    }

    [Test]
    public async Task Should_use_id_field_for_event_key()
    {
        // The whole point of mapping EventId -> _id is that a document's natural key
        // is the event id, so dedup / idempotent inserts and ordered replay work.
        var ev = new WireDeletedEvent
        {
            EventId = Guid.Parse("00000020-000b-7000-8000-0000000000bb"),
            CommandId = Guid.Empty,
            WireId = Guid.Empty,
        };
        var doc = ((Event)ev).ToBsonDocument(typeof(Event));
        await Assert.That(doc.Contains("_id")).IsTrue();
        await Assert.That(doc.Contains("EventId")).IsFalse(); // mapped to _id, not duplicated
        await Assert.That(doc["_id"].AsGuid).IsEqualTo(ev.EventId);
    }
}
