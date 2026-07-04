using MongoDB.Bson;
using MongoDB.Driver;
using Synqra.AppendStorage.MongoDb;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// <see cref="MongoEventSidBackfill"/> upgrades event logs written before StreamId was persisted
/// as _sid. The legacy documents here are inserted raw (BsonDocument, no _sid) to reproduce the
/// exact production shape: a CommandCreatedEvent whose embedded command names its stream (pass 1
/// join source), sibling events sharing its CommandId, a command-less event resolvable only via
/// its target's materialized projection document (pass 2), and one genuinely unresolvable event
/// that must be left alone and reported rather than guessed at.
/// </summary>
public class MongoEventSidBackfillTests : BaseTest
{
	IMongoDatabase Db()
	{
		var url = new MongoUrl(_connectionString);
		return new MongoClient(url).GetDatabase(url.DatabaseName);
	}

	static BsonBinaryData Bin(Guid g) => new(g, GuidRepresentation.Standard);

	[Test]
	public async Task Should_backfill_sid_from_command_join_and_projection_fallback()
	{
		var db = Db();
		var events = db.GetCollection<BsonDocument>("Event");

		var streamA = Guid.NewGuid(); // via CommandCreatedEvent join
		var streamB = Guid.NewGuid(); // via projection fallback
		var commandId = Guid.NewGuid();
		var joinedEventId = Guid.NewGuid();
		var cceEventId = Guid.NewGuid();
		var seededEventId = Guid.NewGuid();
		var seededTargetId = Guid.NewGuid();
		var orphanEventId = Guid.NewGuid();

		await events.InsertManyAsync(
		[
			// Pass-1 pair: the CommandCreatedEvent names its stream in the embedded command;
			// the sibling event only shares the CommandId.
			new BsonDocument
			{
				["_id"] = Bin(cceEventId),
				["_t"] = "CommandCreatedEvent",
				["CommandId"] = Bin(commandId),
				["Data"] = new BsonDocument { ["_t"] = "CreateObjectCommand", ["StreamId"] = Bin(streamA), ["TargetId"] = Bin(Guid.NewGuid()) },
			},
			new BsonDocument
			{
				["_id"] = Bin(joinedEventId),
				["_t"] = "ObjectCreatedEvent",
				["CommandId"] = Bin(commandId),
				["TargetId"] = Bin(Guid.NewGuid()),
			},
			// Pass-2: no CommandCreatedEvent for this CommandId anywhere — resolved through the
			// target's materialized projection document below.
			new BsonDocument
			{
				["_id"] = Bin(seededEventId),
				["_t"] = "ComponentAddedEvent",
				["CommandId"] = Bin(Guid.NewGuid()),
				["TargetId"] = Bin(seededTargetId),
			},
			// Unresolvable: unknown command, target absent from every projection collection.
			new BsonDocument
			{
				["_id"] = Bin(orphanEventId),
				["_t"] = "ObjectCreatedEvent",
				["CommandId"] = Bin(Guid.NewGuid()),
				["TargetId"] = Bin(Guid.NewGuid()),
			},
		]);

		// The projection half (same ephemeral database here, like production tests do): the
		// seeded target's materialized Node document already carries _sid.
		await db.GetCollection<BsonDocument>("Node").InsertOneAsync(new BsonDocument
		{
			["_id"] = Bin(seededTargetId),
			["_t"] = "Node",
			["_sid"] = Bin(streamB),
		});

		var (backfilled, unresolved) = await MongoEventSidBackfill.BackfillAsync(db, db);
		await Assert.That(backfilled).IsEqualTo(3);
		await Assert.That(unresolved).IsEqualTo(1);

		async Task<BsonDocument> Doc(Guid id) => await events.Find(Builders<BsonDocument>.Filter.Eq("_id", Bin(id))).SingleAsync();
		await Assert.That((await Doc(cceEventId))["_sid"].AsGuid).IsEqualTo(streamA);
		await Assert.That((await Doc(joinedEventId))["_sid"].AsGuid).IsEqualTo(streamA);
		await Assert.That((await Doc(seededEventId))["_sid"].AsGuid).IsEqualTo(streamB);
		await Assert.That((await Doc(orphanEventId)).Contains("_sid")).IsFalse();

		// Idempotent: a second run touches nothing new (the orphan stays reported, not guessed).
		var (again, stillUnresolved) = await MongoEventSidBackfill.BackfillAsync(db, db);
		await Assert.That(again).IsEqualTo(0);
		await Assert.That(stillUnresolved).IsEqualTo(1);
	}

	[Test]
	public async Task Should_no_op_on_clean_log()
	{
		var db = Db();
		await db.GetCollection<BsonDocument>("Event").InsertOneAsync(new BsonDocument
		{
			["_id"] = Bin(Guid.NewGuid()),
			["_t"] = "ObjectCreatedEvent",
			["CommandId"] = Bin(Guid.NewGuid()),
			["_sid"] = Bin(Guid.NewGuid()),
		});
		var (backfilled, unresolved) = await MongoEventSidBackfill.BackfillAsync(db, db);
		await Assert.That(backfilled).IsEqualTo(0);
		await Assert.That(unresolved).IsEqualTo(0);
	}
}
