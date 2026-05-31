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
/// <c>mongod</c>. Uses Mongo2Go to spin up a self-contained, ephemeral MongoDB
/// (no external service / Docker dependency — same spirit as the connection-string
/// injection Quotaly uses for its Mongo tests, except the server is bundled). The
/// connection string is injected through configuration
/// (<c>Storage:MongoDbAppendStorage:ConnectionString</c>), exactly the path the
/// production DI extension binds.
/// <para>
/// If the bundled mongod cannot start in this environment (e.g. a CI image lacking
/// the shared libraries it needs), the tests <b>skip</b> rather than fail — they prove
/// the behavior wherever a mongod can run, without turning CI red where it can't.
/// </para>
/// </summary>
[NotInParallel]
public class MongoAppendStorageTests : BaseTest
{
    MongoDbRunner? _runner;
    string? _connectionString;
    string _databaseName = "synqra-mongo-tests";
    string? _skipReason;

    [Before(Test)]
    public void StartMongo()
    {
        _databaseName = "synqra-mongo-tests-" + GuidExtensions.CreateVersion7().ToString("N");
        try
        {
            _runner = MongoDbRunner.Start(singleNodeReplSet: false);
            _connectionString = _runner.ConnectionString;
        }
        catch (Exception ex)
        {
            _skipReason = "Bundled mongod could not start in this environment: " + ex.Message;
        }
    }

    [After(Test)]
    public void StopMongo()
    {
        _runner?.Dispose();
        _runner = null;
    }

    protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
    {
        base.Register(hostApplicationBuilder);
        if (_connectionString is null)
        {
            return; // skipped — nothing to wire
        }
        // Inject the connection string + an isolated database the same way the
        // production options bind it. Set inside Register so it survives Reopen()
        // (Restart rebuilds the host and re-runs Register).
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
        if (_skipReason is not null)
        {
            throw new SkipTestException(_skipReason);
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
