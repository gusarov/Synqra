using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Synqra.AppendStorage;
using Synqra.AppendStorage.MongoDb;
using Synqra.Projection.InMemory;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.AssertionBuilders.Wrappers;
using TUnit.Assertions.Extensions;
using TUnit.Core.Exceptions;

namespace Synqra.Tests;

public class MongoTestsIsolationAssumptions : BaseTest
{
	[Test]
	public async Task Should_1()
	{
		var url = new MongoUrl(_connectionString);
		using var client = new MongoClient(url);
		var db = client.GetDatabase(url.DatabaseName);
		var test = db.GetCollection<BsonDocument>("test");
		await test.InsertOneAsync(new BsonDocument { ["_id"] = 1 });
		Console.WriteLine(GetHashCode());
		Console.WriteLine(_connectionString);
		Console.WriteLine(_connectionString);
	}

	[Test]
	public async Task Should_2()
	{
		await Should_1();
	}

	[Test]
	public async Task Should_3()
	{
		await Should_1();
	}

	[Test]
	public async Task Should_4_drop_a_stale_temp_database_once_when_sweeps_race()
	{
		var prefix = $"temp_sweep{Guid.NewGuid():N}"[..18] + "_"; // unique per run: parallel test processes run this test at the same time
		using var client = new MongoClient(new MongoUrlBuilder(_connectionString) { DatabaseName = null }.ToMongoUrl());
		var ms = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-5)).ToUnixTimeMilliseconds().ToString("x12");
		var rnd = Guid.NewGuid().ToString("N");
		var stale = prefix + $"{ms[..8]}_{ms[8..]}_7{rnd[13..16]}_8{rnd[17..20]}_{rnd[20..]}";
		await client.GetDatabase(stale).GetCollection<BsonDocument>("payload").InsertManyAsync(Enumerable.Range(0, 200).Select(i => new BsonDocument { ["_id"] = i }));

		using var start = new ManualResetEventSlim();
		var sweeps = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
		{
			start.Wait();
			return new TempDatabaseSweeper(client, prefix).Sweep(DateTime.UtcNow);
		})).ToArray();
		start.Set();
		var dropped = await Task.WhenAll(sweeps);

		await Assert.That(dropped.Sum()).IsEqualTo(1);
		await Assert.That((await client.ListDatabaseNamesAsync()).ToList()).DoesNotContain(stale);
	}
}

/// <summary>
/// Integration tests for <see cref="MongoAppendStorage{T,TKey}"/> against a real
/// <c>mongod</c> (see <see cref="EphemeralMongo"/>). The connection string is injected
/// through configuration (<c>Storage:MongoDbAppendStorage:ConnectionString</c>), the exact
/// path the production DI extension binds. The events used are the regular core events
/// (<see cref="ObjectPropertyChangedEvent"/>), not any experimental type.
/// <para>
/// If the bundled mongod cannot start here, the tests <b>skip</b> rather than fail.
/// </para>
/// </summary>
public class MongoAppendStorageTests : BaseTest
{
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.Services.AddTestMongoDatabase(_connectionString).AddMongoEventStorage().AddServerEventLog();
	}

	void Reopen()
	{
		Restart();
		Console.WriteLine("Reopened (new client/host, same database)");
	}

	readonly Guid _stream = Guid.NewGuid();

	IEventLog Storage()
	{
		if (_connectionString is null)
		{
			throw new Exception("Mongo is not configured 3");
			// throw new SkipTestException(EphemeralMongo.SkipReason);
		}
		return ServiceProvider.GetRequiredService<IEventLog>();
	}

	// A regular, payload-light core event that carries an assertable value, on this test's stream unless one is given.
	ObjectPropertyChangedEvent Change(Guid id, string value, Guid streamId = default)
	{
		return new ObjectPropertyChangedEvent
		{
			EventId = id,
			CommandId = GuidExtensions.CreateVersion7(),
			StreamId = streamId == default ? _stream : streamId,
			TargetId = Guid.NewGuid(),
			TargetTypeId = Guid.NewGuid(),
			CollectionId = Guid.NewGuid(),
			PropertyName = "Name",
			NewValue = value,
		};
	}

	[Test]
	public async Task Should_M00_be_empty()
	{
		var storage = Storage();
		await Assert.That(storage.GetAllAsync().ToBlockingEnumerable().Count()).IsEqualTo(0);
	}

	[Test]
	public async Task Should_M10_append_and_read_survives_reopen()
	{
		var storage = Storage();
		var ev = Change(GuidExtensions.CreateVersion7(), "Alice");
		await storage.AppendAsync(ev);

		var items = storage.GetAllAsync().ToBlockingEnumerable().ToArray();
		await Assert.That(items.Length).IsEqualTo(1);
		await Assert.That((string?)((ObjectPropertyChangedEvent)items[0]).NewValue).IsEqualTo("Alice");

		// Durability: a fresh client/host against the same database still sees it.
		Reopen();
		storage = Storage();
		items = storage.GetAllAsync().ToBlockingEnumerable().ToArray();
		await Assert.That(items.Length).IsEqualTo(1);
		await Assert.That(items[0].EventId).IsEqualTo(ev.EventId);
		await Assert.That((string?)((ObjectPropertyChangedEvent)items[0]).NewValue).IsEqualTo("Alice");
	}

	[Test]
	public async Task Should_M11_replay_in_commit_order()
	{
		var storage = Storage();
		var gen = new GuidExtensions.Generator();
		var e1 = Change(gen.CreateVersion7(), "one");
		var e2 = Change(gen.CreateVersion7(), "two");
		var e3 = Change(gen.CreateVersion7(), "three");

		// Appended out of id order: the log's order is the commit order (docs/model.md §9), the late e1 fast-forwarded past e2.
		await storage.AppendAsync(e2);
		await storage.AppendAsync(e1);
		await storage.AppendAsync(e3);

		var ids = storage.GetAllAsync().ToBlockingEnumerable().Select(x => x.EventId).ToArray();
		await Assert.That(ids).IsEquivalentTo(new[] { e2.EventId, e1.EventId, e3.EventId });
		await Assert.That(e1.SequenceId).IsNotEqualTo(e1.EventId);
		await Assert.That(e3.SequenceId.CompareBigEndian(e1.SequenceId)).IsGreaterThan(0); // minted in one burst: e3 may be bumped too, but stays last
	}

	[Test]
	public async Task Should_M14_get_by_key()
	{
		var storage = Storage();
		var ev = Change(GuidExtensions.CreateVersion7(), "Carol");
		await storage.AppendAsync(ev);

		var back = await storage.GetAsync((ev.StreamId, ev.SequenceId));
		await Assert.That(back.EventId).IsEqualTo(ev.EventId);
		await Assert.That((string?)((ObjectPropertyChangedEvent)back).NewValue).IsEqualTo("Carol");
	}

	[Test]
	public async Task Should_M20_duplicate_append_is_idempotent()
	{
		var storage = Storage();
		var ev = Change(GuidExtensions.CreateVersion7(), "dup");
		await storage.AppendAsync(ev);
		await storage.AppendAsync(ev); // same event id again — must not throw, must not duplicate

		await Assert.That(storage.GetAllAsync().ToBlockingEnumerable().Count()).IsEqualTo(1);
	}

	[Test]
	public async Task Should_M21_batch_with_mid_duplicate_still_inserts_new()
	{
		var storage = Storage();
		var gen = new GuidExtensions.Generator();
		var e1 = Change(gen.CreateVersion7(), "one");
		var e2 = Change(gen.CreateVersion7(), "two");
		var e3 = Change(gen.CreateVersion7(), "three");

		await storage.AppendAsync(e1);
		// e1 is a duplicate inside the batch; e2 + e3 are new. Unordered insert must
		// still land e2 and e3 (the IsOrdered=false semantics).
		await storage.AppendBatchAsync(new Event[] { e1, e2, e3 });

		var ids = storage.GetAllAsync().ToBlockingEnumerable().Select(x => x.EventId).ToArray();
		await Assert.That(ids.Length).IsEqualTo(3);
		await Assert.That(ids).Contains(e2.EventId);
		await Assert.That(ids).Contains(e3.EventId);
	}

	[Test]
	public async Task Should_M30_from_paging_returns_tail_and_null_returns_all()
	{
		var storage = Storage();
		var gen = new GuidExtensions.Generator();
		var e1 = Change(gen.CreateVersion7(), "one");
		var e2 = Change(gen.CreateVersion7(), "two");
		var e3 = Change(gen.CreateVersion7(), "three");
		await storage.AppendAsync(e1);
		await storage.AppendAsync(e2);
		await storage.AppendAsync(e3);

		// No `from` (default) must return everything (the null-handling fix).
		await Assert.That(storage.GetAllAsync().ToBlockingEnumerable().Count()).IsEqualTo(3);

		// from = e2's key is a cursor (the caller holds e2): the tail after it, e3.
		var tail = storage.GetAllAsync((_stream, e2.SequenceId)).ToBlockingEnumerable().Select(x => x.EventId).ToArray();
		await Assert.That(tail).IsEquivalentTo(new[] { e3.EventId });
	}

	[Test]
	public async Task Should_M40_stream_id_round_trips_and_persists_as_sid()
	{
		var storage = Storage();
		var stream = Guid.NewGuid();
		var ev = Change(GuidExtensions.CreateVersion7(), "scoped", stream);
		await storage.AppendAsync(ev);

		// Round-trip: the event's stream survives persistence. Before StreamId was mapped to
		// _sid, MongoEventClassMaps unmapped it (matching the JSON log), so it came back
		// Guid.Empty here and stream isolation had nothing to filter on.
		var back = storage.GetAllAsync().ToBlockingEnumerable().Single();
		await Assert.That(back.StreamId).IsEqualTo(stream);

		// And it lands in the durable document as "_sid" — the same stream column
		// MongoProjection stamps on its read-model docs, so an event and its materialized
		// state read alike.
		var url = new MongoUrl(_connectionString);
		var doc = await new MongoClient(url).GetDatabase(url.DatabaseName)
			.GetCollection<BsonDocument>("Event")
			.Find(Builders<BsonDocument>.Filter.Eq("_id", ev.SequenceId)).SingleAsync();
		await Assert.That(doc.Contains("_sid")).IsTrue();
		await Assert.That(doc["_sid"].AsGuid).IsEqualTo(stream);
	}

	[Test]
	public async Task Should_M42_event_log_isolates_two_streams_in_one_collection()
	{
		var storage = Storage();
		var streamA = Guid.NewGuid();
		var streamB = Guid.NewGuid();

		// One shared multitenant collection — the production shape: a stream is the key's prefix.
		var a = Change(GuidExtensions.CreateVersion7(), "a", streamA);
		var b = Change(GuidExtensions.CreateVersion7(), "b", streamB);
		await storage.AppendAsync(a);
		await storage.AppendAsync(b);

		// Each stream reads back ONLY its own events, whichever of the two sorts first.
		await Assert.That(await ReadIds(storage, streamA)).IsEquivalentTo(new[] { a.EventId });
		await Assert.That(await ReadIds(storage, streamB)).IsEquivalentTo(new[] { b.EventId });
	}

	async Task<List<Guid>> ReadIds(IEventLog log, Guid streamId)
	{
		var ids = new List<Guid>();
		await foreach (var ev in log.ReadStreamAsync(streamId))
		{
			ids.Add(ev.EventId);
		}
		return ids;
	}
}
