using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Synqra.AppendStorage;
using Synqra.AppendStorage.InMemory;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;
using Synqra.BlobStorage.File;
using Synqra.Projection.InMemory;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

//// MEMO
/// Why Cobra?
/// Because I were adding 11 classes in 1 shot and LLM named them with service type prefixes, so very hard to find in tree. Cobra is a codename for this entire testing effort.

namespace Synqra.Tests.Cobra;

/// <summary>
/// The <b>same-session</b> contract shared by every Synqra store, on every backend: a property change
/// on a tracked model becomes an <see cref="ObjectPropertyChangedEvent"/> in the event log. This holds
/// for durable <i>and</i> non-durable storage alike, so all backends inherit it.
/// <para>
/// The stronger <b>durability</b> clause (state survives a restart via replay) lives separately in
/// <see cref="DurableSynqraStoreContractTests"/> — only durable storage backends inherit that, so a
/// non-durable in-memory log is never falsely marked red for failing to persist.
/// </para>
/// <para>
/// Concrete subclasses fill in the two independent axes of the permutation matrix:
/// </para>
/// <list type="bullet">
///   <item><b>Event storage</b> (<see cref="RegisterAppendStorage"/>): MongoDB / SBX blob files / in-memory.</item>
///   <item><b>Store/projection</b> (<see cref="RegisterStore"/>): in-memory projection / MongoDB projection.</item>
/// </list>
/// <para>
/// Cells whose implementation does not exist yet (in-memory <c>IAppendStorage</c>, MongoDB
/// projection) deliberately fail — they are the red end of TDD and name the API to build.
/// </para>
/// </summary>
public abstract class Cobra_SynqraStoreContractTests : BaseTest
{
	/// <summary>Wire up the durable event log under test — registers <c>IAppendStorage&lt;Event, Guid&gt;</c>.</summary>
	protected abstract void RegisterAppendStorage(IHostApplicationBuilder hostBuilder);

	/// <summary>Wire up the store/projection under test — registers <c>IObjectStore</c> / <c>IProjection</c>.</summary>
	protected abstract void RegisterStore(IServiceCollection services);

	protected override void Register(IHostApplicationBuilder hostBuilder)
	{
		base.Register(hostBuilder);

		hostBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default);
		hostBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions);
		hostBuilder.Services.AddTypeMetadataProvider(
		[
			typeof(DemoModel),
			typeof(Command),
			typeof(CreateObjectCommand),
			typeof(ChangeObjectPropertyCommand),
		]);
		hostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Snapshot();
		});

		RegisterAppendStorage(hostBuilder);
		RegisterStore(hostBuilder.Services);
	}

	/// <summary>
	/// The store under test. Command-event persistence is turned off so the durable log keeps only
	/// domain events (which carry no live CLR payload) — uniform across all backends.
	/// </summary>
	protected IObjectStore ResolveStore()
	{
		var store = ServiceProvider.GetRequiredService<IObjectStore>();
		if (store is InMemoryProjection projection)
		{
			projection.PersistCommandEvents = false;
		}
		return store;
	}

	protected IAppendStorage<Event, Guid> Events() => ServiceProvider.GetRequiredService<IAppendStorage<Event, Guid>>();

	[Test]
	public async Task Should_10_persist_property_change_to_event_log()
	{
		var store = ResolveStore();
		var model = new DemoModel();
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);

		model.Name = "Alice"; // generated setter -> ChangeObjectPropertyCommand -> ObjectPropertyChangedEvent -> storage

		var changes = Events().GetAllAsync().ToBlockingEnumerable()
			.OfType<ObjectPropertyChangedEvent>()
			.Where(e => e.TargetId == id && e.PropertyName == nameof(DemoModel.Name))
			.ToArray();

		await Assert.That(changes.Length).IsGreaterThanOrEqualTo(1);
		await Assert.That((string?)changes[^1].NewValue).IsEqualTo("Alice");
	}
}

/// <summary>
/// Adds the <b>durability</b> clause to <see cref="Cobra_SynqraStoreContractTests"/>: state must survive a
/// full restart and come back by replaying the event log alone. This is a property of a <i>durable</i>
/// event storage (MongoDB, SBX files) — a non-durable (in-memory) log can't satisfy it, so only
/// durable storage backends inherit this contract. Pure derivation, no extra wiring.
/// </summary>
public abstract class Cobra_DurableSynqraStoreContractTests : Cobra_SynqraStoreContractTests
{
	[Test]
	public async Task Should_20_state_survives_restart_via_replay()
	{
		var store = ResolveStore();
		var model = new DemoModel();
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);
		model.Name = "Bob";

		// New host + new store against the same durable log — state must come back by replay alone.
		Restart();

		var store2 = ResolveStore();
		if (store2 is InMemoryProjection projection)
		{
			await projection.LoadStateAsync();
		}

		var reloaded = store2.GetCollection<DemoModel>().FirstOrDefault(m => store2.GetId(m) == id);
		await Assert.That(reloaded).IsNotNull();
		await Assert.That(reloaded!.Name).IsEqualTo("Bob");
	}
}

// =====================================================================================
// Event-storage axis (abstract — fixes IAppendStorage<Event, Guid>, leaves the store open)
// =====================================================================================

/// <summary>Event log = MongoDB (native, queryable BSON documents) — durable.</summary>
public abstract class Cobra_MongoEventStorageContract : Cobra_DurableSynqraStoreContractTests
{
	// EphemeralMongo prefers the user-secrets "Mongodb" connection (the developer's local Mongo),
	// falling back to a Mongo2Go instance. Captured once so it stays stable across Restart().
	readonly string _connectionString = EphemeralMongo.ConnectionString
		?? throw new global::System.Exception("Mongo is not available for the matrix tests");

	// Unique db per test fixture; dropped before the test so each run starts clean. Stable across
	// Restart() so the replay test reads the same database it wrote.
	readonly string _databaseName = "synqra-matrix-" + Guid.NewGuid().ToString("N");

	[Before(Test)]
	public void DropDatabase() => new MongoClient(_connectionString).DropDatabase(_databaseName);

	protected override void RegisterAppendStorage(IHostApplicationBuilder hostBuilder)
	{
		Configuration["Storage:MongoDbAppendStorage:ConnectionString"] = _connectionString;
		Configuration["Storage:MongoDbAppendStorage:DatabaseName"] = _databaseName;
		hostBuilder.AddAppendStorageMongoDb<Event>();
	}
}

/// <summary>Event log = SBX-serialized blobs on the local filesystem — durable.</summary>
public abstract class Cobra_SbxFileEventStorageContract : Cobra_DurableSynqraStoreContractTests
{
	string? _folder;

	protected override void RegisterAppendStorage(IHostApplicationBuilder hostBuilder)
	{
		_folder ??= CreateTestFolder(); // stable across Restart() so replay reads the same files
		Configuration["Storage:BlobStorage:File:Folder"] = Path.Combine(_folder, "[Store]") + Path.DirectorySeparatorChar;
		hostBuilder.AddBlobStorageFile<Event>(x => x.EventId);
	}
}

/// <summary>
/// Event log = in-memory. RED: there is no in-memory <c>IAppendStorage&lt;Event, Guid&gt;</c> yet.
/// Intended API to build: <c>hostBuilder.AddAppendStorageInMemory&lt;Event&gt;()</c>.
/// </summary>
public abstract class Cobra_InMemoryEventStorageContract : Cobra_SynqraStoreContractTests
{
	protected override void RegisterAppendStorage(IHostApplicationBuilder hostBuilder) => InMemoryAppendStorageExtensions.AddInMemoryAppendStorage<Event, Guid>(hostBuilder, x => x.EventId);
}

// =====================================================================================
// Permutation matrix (storage axis × store axis)
// =====================================================================================

[InheritsTests]
[NotInParallel]
[Property("CI", "false")]
public sealed class Cobra_Mongo_Storage_With_InMemory_Store : Cobra_MongoEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services) => services.AddInMemorySynqraStore();
}

[InheritsTests]
[NotInParallel]
[Property("CI", "false")]
public sealed class Cobra_Mongo_Storage_With_Mongo_Store : Cobra_MongoEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services)
		=> throw new global::System.NotImplementedException(
			"RED: no MongoDB IObjectStore/IProjection yet (Synqra.Projection.MongoDb is a stub). Build it and expose AddMongoDbSynqraStore().");
}

[InheritsTests]
public sealed class Cobra_SbxFile_Storage_With_InMemory_Store : Cobra_SbxFileEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services) => services.AddInMemorySynqraStore();
}

[InheritsTests]
public sealed class Cobra_SbxFile_Storage_With_Mongo_Store : Cobra_SbxFileEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services)
		=> throw new global::System.NotImplementedException(
			"RED: no MongoDB IObjectStore/IProjection yet (Synqra.Projection.MongoDb is a stub). Build it and expose AddMongoDbSynqraStore().");
}

[InheritsTests]
public sealed class Cobra_InMemory_Storage_With_InMemory_Store : Cobra_InMemoryEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services) => services.AddInMemorySynqraStore();
}

[InheritsTests]
public sealed class Cobra_InMemory_Storage_With_Mongo_Store : Cobra_InMemoryEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services)
		=> throw new global::System.NotImplementedException(
			"RED: no MongoDB IObjectStore/IProjection yet (Synqra.Projection.MongoDb is a stub). Build it and expose AddMongoDbSynqraStore().");
}
