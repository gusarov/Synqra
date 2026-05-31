using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.AppendStorage.MongoDb;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;
using TUnit.Core.Exceptions;

namespace Synqra.Tests;

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
[NotInParallel]
public class MongoAppendStorageTests : BaseTest
{
	string _databaseName = "synqra-mongo-tests";
	string? _connectionString;

	[Before(Test)]
	public void Setup()
	{
		_databaseName = "synqra-mongo-tests-" + GuidExtensions.CreateVersion7().ToString("N");
		_connectionString = EphemeralMongo.ConnectionString;
	}

	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		if (_connectionString is null)
		{
			return; // skipped — nothing to wire
		}
		// Inject the connection string + an isolated database the way the production
		// options bind it. Set inside Register so it survives Reopen() (Restart rebuilds
		// the host and re-runs Register against the same shared server + db name).
		Configuration["Storage:MongoDbAppendStorage:ConnectionString"] = _connectionString;
		Configuration["Storage:MongoDbAppendStorage:DatabaseName"] = _databaseName;
		hostApplicationBuilder.AddAppendStorageMongoDb<Event>();
	}

	void Reopen()
	{
		Restart();
		Console.WriteLine("Reopened (new client/host, same database)");
	}

	IAppendStorage<Event, Guid> Storage()
	{
		if (_connectionString is null)
		{
			throw new SkipTestException(EphemeralMongo.SkipReason);
		}
		return ServiceProvider.GetRequiredService<IAppendStorage<Event, Guid>>();
	}

	// A regular, payload-light core event that carries an assertable value.
	static ObjectPropertyChangedEvent Change(Guid id, string value)
	{
		return new ObjectPropertyChangedEvent
		{
			EventId = id,
			CommandId = GuidExtensions.CreateVersion7(),
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
	public async Task Should_M11_replay_in_id_order()
	{
		var storage = Storage();
		var gen = new GuidExtensions.Generator();
		var e1 = Change(gen.CreateVersion7(), "one");
		var e2 = Change(gen.CreateVersion7(), "two");
		var e3 = Change(gen.CreateVersion7(), "three");

		// Append out of order — replay must still be by _id (append order).
		await storage.AppendAsync(e2);
		await storage.AppendAsync(e1);
		await storage.AppendAsync(e3);

		var ids = storage.GetAllAsync().ToBlockingEnumerable().Select(x => x.EventId).ToArray();
		await Assert.That(ids).IsEquivalentTo(new[] { e1.EventId, e2.EventId, e3.EventId });
	}

	[Test]
	public async Task Should_M14_get_by_key()
	{
		var storage = Storage();
		var ev = Change(GuidExtensions.CreateVersion7(), "Carol");
		await storage.AppendAsync(ev);

		var back = await storage.GetAsync(ev.EventId);
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

		// from = e2 is inclusive (Gte on _id): e2, e3.
		var tail = storage.GetAllAsync(e2.EventId).ToBlockingEnumerable().Select(x => x.EventId).ToArray();
		await Assert.That(tail).IsEquivalentTo(new[] { e2.EventId, e3.EventId });
	}
}
