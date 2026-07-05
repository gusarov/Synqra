using MongoDB.Bson;
using MongoDB.Driver;
using Synqra.AppendStorage.MongoDb;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// <see cref="SynqraMongoUpgradeService"/> auto-upgrades whatever Mongo storage Synqra opens —
/// exercised here exactly the way a host runs it (the hosted service with announced
/// participants), never by calling an upgrade directly. Covers the first real upgrade
/// (event-sid-backfill: CommandCreatedEvent join + projection fallback, unresolvable left
/// alone), the <c>_synqra_upgrades</c> claim bookkeeping, idempotent re-runs, and the
/// single-winner guarantee under concurrent nodes.
/// </summary>
public class MongoEventSidBackfillTests : BaseTest
{
	IMongoDatabase Db()
	{
		var url = new MongoUrl(_connectionString);
		return new MongoClient(url).GetDatabase(url.DatabaseName);
	}

	static BsonBinaryData Bin(Guid g) => new(g, GuidRepresentation.Standard);

	static SynqraMongoUpgradeService Runner(IMongoDatabase db) => new(
	[
		new SynqraMongoUpgradeParticipant(null, SynqraMongoDatabaseRole.Events, _ => db, "Event"),
		new SynqraMongoUpgradeParticipant(null, SynqraMongoDatabaseRole.Projection, _ => db),
	], null!);

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

		await Runner(db).StartAsync(CancellationToken.None);

		async Task<BsonDocument> Doc(Guid id) => await events.Find(Builders<BsonDocument>.Filter.Eq("_id", Bin(id))).SingleAsync();
		await Assert.That((await Doc(cceEventId))["_sid"].AsGuid).IsEqualTo(streamA);
		await Assert.That((await Doc(joinedEventId))["_sid"].AsGuid).IsEqualTo(streamA);
		await Assert.That((await Doc(seededEventId))["_sid"].AsGuid).IsEqualTo(streamB);
		await Assert.That((await Doc(orphanEventId)).Contains("_sid")).IsFalse();

		// The claim is recorded and completed in _synqra_upgrades — that's both the audit trail
		// and what makes the next boot skip without racing.
		var claim = await db.GetCollection<BsonDocument>("_synqra_upgrades")
			.Find(Builders<BsonDocument>.Filter.Eq("_id", "event-sid-backfill/1")).SingleAsync();
		await Assert.That(claim.Contains("completedAt")).IsTrue();

		// Second boot: completed claim short-circuits; the orphan stays untouched, not guessed.
		await Runner(db).StartAsync(CancellationToken.None);
		await Assert.That((await Doc(orphanEventId)).Contains("_sid")).IsFalse();
	}

	[Test]
	public async Task Should_let_exactly_one_concurrent_node_win_the_claim()
	{
		var db = Db();
		var events = db.GetCollection<BsonDocument>("Event");
		var stream = Guid.NewGuid();
		var commandId = Guid.NewGuid();
		await events.InsertManyAsync(
		[
			new BsonDocument
			{
				["_id"] = Bin(Guid.NewGuid()),
				["_t"] = "CommandCreatedEvent",
				["CommandId"] = Bin(commandId),
				["Data"] = new BsonDocument { ["_t"] = "CreateObjectCommand", ["StreamId"] = Bin(stream) },
			},
		]);

		// Several "nodes" (independent runner instances) boot at once against the same
		// database. The unique _id insert into _synqra_upgrades lets exactly one win; the rest
		// wait for its completion marker. Everyone returns, nothing throws, one claim doc.
		await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => Runner(db).StartAsync(CancellationToken.None))));

		var claims = await db.GetCollection<BsonDocument>("_synqra_upgrades")
			.Find(Builders<BsonDocument>.Filter.Eq("_id", "event-sid-backfill/1")).ToListAsync();
		await Assert.That(claims.Count).IsEqualTo(1);
		await Assert.That(claims[0].Contains("completedAt")).IsTrue();
		var cce = await events.Find(Builders<BsonDocument>.Filter.Eq("_t", "CommandCreatedEvent")).SingleAsync();
		await Assert.That(cce["_sid"].AsGuid).IsEqualTo(stream);
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
		await Runner(db).StartAsync(CancellationToken.None);
		var claim = await db.GetCollection<BsonDocument>("_synqra_upgrades")
			.Find(Builders<BsonDocument>.Filter.Eq("_id", "event-sid-backfill/1")).SingleAsync();
		await Assert.That(claim.Contains("completedAt")).IsTrue();
	}
}
