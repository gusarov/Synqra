using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mongo2Go;
using Synqra.AppendStorage;
using Synqra.AppendStorage.MongoDb;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;
using TUnit.Core.Exceptions;

namespace Synqra.Tests;

/// <summary>
/// Integration tests for <see cref="MongoAppendStorage{T,TKey}"/> against a real
/// <c>mongod</c>, provided by Mongo2Go — the same ephemeral-Mongo mechanism Quotaly's
/// <c>IntegrationTestBase</c> uses. The connection string is injected through
/// configuration (<c>Storage:MongoDbAppendStorage:ConnectionString</c>), the exact path
/// the production DI extension binds.
/// <para>
/// Following Quotaly's hard-won pattern, a single <see cref="MongoDbRunner"/> is started
/// once per test process and reused (not one-per-test): mongod startup is slow and
/// re-binding ports is flaky, so sharing is both faster and steadier. The runner is
/// deliberately never disposed per-test — Mongo2Go's finalizer reaps it at process exit,
/// and (on Windows dev boxes) stale mongod processes are swept on first use. Per-test
/// isolation comes from a unique database name instead.
/// </para>
/// <para>
/// If the bundled mongod cannot start here (e.g. a CI image lacking its shared libraries)
/// the tests <b>skip</b> rather than fail.
/// </para>
/// </summary>
[NotInParallel]
public class MongoAppendStorageTests : BaseTest
{
	static readonly object _sync = new();
	static MongoDbRunner? _runner;
	static string? _skipReason;

	/// <summary>
	/// Lazily start (once) the shared ephemeral mongod and return its connection string,
	/// or null if it could not be started (tests then skip). Mirrors the shared-runner
	/// approach in Quotaly's IntegrationTestBase.
	/// </summary>
	static string? SharedConnectionString()
	{
		if (_runner is not null) return _runner.ConnectionString;
		if (_skipReason is not null) return null;
		lock (_sync)
		{
			if (_runner is not null) return _runner.ConnectionString;
			if (_skipReason is not null) return null;
			try
			{
				SweepStaleMongodOnWindows();
				// Standalone (not a replica set): the append-storage path does only plain
				// inserts/finds — no multi-document transactions — so it doesn't need the
				// replica set Quotaly's app-level base starts (that base uses
				// singleNodeReplSet: true because the application itself runs transactions).
				// Standalone also starts faster and more reliably.
				_runner = MongoDbRunner.Start(singleNodeReplSet: false);
				return _runner.ConnectionString;
			}
			catch (Exception ex)
			{
				_skipReason = "Bundled mongod could not start in this environment: " + ex.Message;
				return null;
			}
		}
	}

	[Conditional("DEBUG")]
	static void SweepStaleMongodOnWindows()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
		// A never-disposed shared runner can leave a mongod behind across runs on dev
		// machines; sweep ones older than a minute (never elevated, so we can't and won't
		// touch other users' processes).
		foreach (var p in Process.GetProcessesByName("mongod").Concat(Process.GetProcessesByName("mongod.exe")))
		{
			try
			{
				if (DateTime.UtcNow - p.StartTime.ToUniversalTime() > TimeSpan.FromMinutes(1))
				{
					p.Kill();
				}
			}
			catch (System.ComponentModel.Win32Exception)
			{
				// Owned by another user — skip, let Mongo2Go work around it.
			}
		}
	}

	string _databaseName = "synqra-mongo-tests";
	string? _connectionString;

	[Before(Test)]
	public void Setup()
	{
		_databaseName = "synqra-mongo-tests-" + GuidExtensions.CreateVersion7().ToString("N");
		_connectionString = SharedConnectionString();
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
			throw new SkipTestException(_skipReason ?? "Ephemeral mongod is unavailable");
		}
		return ServiceProvider.GetRequiredService<IAppendStorage<Event, Guid>>();
	}

	static WireAddedEvent Wire(Guid id) => new()
	{
		EventId = id,
		CommandId = GuidExtensions.CreateVersion7(),
		WireId = GuidExtensions.CreateVersion7(),
		SourceContainerId = Guid.NewGuid(),
		SourceComponentTypeId = Guid.NewGuid(),
		SourcePortName = "out",
		TargetContainerId = Guid.NewGuid(),
		TargetComponentTypeId = Guid.NewGuid(),
		TargetPortName = "in",
		Type = (int)PortType.Event,
	};

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
		var ev = Wire(GuidExtensions.CreateVersion7());
		await storage.AppendAsync(ev);

		var items = storage.GetAllAsync().ToBlockingEnumerable().ToArray();
		await Assert.That(items.Length).IsEqualTo(1);
		await Assert.That(((WireAddedEvent)items[0]).WireId).IsEqualTo(ev.WireId);

		// Durability: a fresh client/host against the same database still sees it.
		Reopen();
		storage = Storage();
		items = storage.GetAllAsync().ToBlockingEnumerable().ToArray();
		await Assert.That(items.Length).IsEqualTo(1);
		await Assert.That(((WireAddedEvent)items[0]).EventId).IsEqualTo(ev.EventId);
	}

	[Test]
	public async Task Should_M11_replay_in_id_order()
	{
		var storage = Storage();
		var gen = new GuidExtensions.Generator();
		var e1 = Wire(gen.CreateVersion7());
		var e2 = Wire(gen.CreateVersion7());
		var e3 = Wire(gen.CreateVersion7());

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
		var ev = Wire(GuidExtensions.CreateVersion7());
		await storage.AppendAsync(ev);

		var back = await storage.GetAsync(ev.EventId);
		await Assert.That(back.EventId).IsEqualTo(ev.EventId);
		await Assert.That(((WireAddedEvent)back).WireId).IsEqualTo(ev.WireId);
	}

	[Test]
	public async Task Should_M20_duplicate_append_is_idempotent()
	{
		var storage = Storage();
		var ev = Wire(GuidExtensions.CreateVersion7());
		await storage.AppendAsync(ev);
		await storage.AppendAsync(ev); // same event id again — must not throw, must not duplicate

		await Assert.That(storage.GetAllAsync().ToBlockingEnumerable().Count()).IsEqualTo(1);
	}

	[Test]
	public async Task Should_M21_batch_with_mid_duplicate_still_inserts_new()
	{
		var storage = Storage();
		var gen = new GuidExtensions.Generator();
		var e1 = Wire(gen.CreateVersion7());
		var e2 = Wire(gen.CreateVersion7());
		var e3 = Wire(gen.CreateVersion7());

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
		var e1 = Wire(gen.CreateVersion7());
		var e2 = Wire(gen.CreateVersion7());
		var e3 = Wire(gen.CreateVersion7());
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
