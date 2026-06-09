using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Synqra.AppendStorage;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;
using Synqra.Projection.InMemory;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;
using TUnit.Core.Exceptions;

namespace Synqra.Tests;

/// <summary>
/// End-to-end: the object store wired to MongoDB append storage. Proves the real CQRS/ES
/// path — assigning a property on a tracked model emits a command, the projection turns it
/// into an event, the event lands in Mongo — and a reopened store replays that event back
/// into materialized state. This is the actual durability story for a Mongo-backed graph,
/// not just storage-level round-trips.
/// <para>
/// <see cref="InMemoryProjection.PersistCommandEvents"/> is turned off: the durable log
/// keeps only the domain events (which carry no live CLR payload), so MongoDB's
/// ObjectSerializer allowlist isn't tripped by a command's <c>TargetObject</c>. State
/// replay needs only those domain events. (Command persistence returns for undo/redo once
/// command payloads are serializable.)
/// </para>
/// <para>
/// Skips when the bundled mongod can't start (see <see cref="EphemeralMongo"/>).
/// </para>
/// </summary>
[NotInParallel]
public class MongoObjectStoreEndToEndTests : BaseTest
{
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);

		hostApplicationBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default);
		hostApplicationBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions);
		hostApplicationBuilder.Services.AddTypeMetadataProvider(
		[
			typeof(DemoModel),
			typeof(Command),
			typeof(CreateObjectCommand),
			typeof(ChangeObjectPropertyCommand),
		]);
		hostApplicationBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Snapshot();
		});

		hostApplicationBuilder.Services.AddAppendStorageMongoDb<Event>(_connectionString);
		hostApplicationBuilder.Services.AddInMemorySynqraStore();
	}

	/// <summary>
	/// The store, with command-event persistence turned off (durable log = domain events only).
	/// </summary>
	InMemoryProjection Projection()
	{
		if (_connectionString is null)
		{
			throw new Exception("Mongo is not configured 4");
		}
		var projection = ServiceProvider.GetRequiredService<InMemoryProjection>();
		projection.PersistCommandEvents = false;
		return projection;
	}

	IAppendStorage<Event, Guid> Events()
	{
		return ServiceProvider.GetRequiredService<IAppendStorage<Event, Guid>>();
	}

	[Test]
	public async Task Should_persist_property_assignment_as_mongo_event()
	{
		var store = (IObjectStore)Projection();
		var model = new DemoModel();
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);

		model.Name = "Alice"; // generated setter -> ChangeObjectPropertyCommand -> ObjectPropertyChangedEvent -> Mongo

		// The change really landed in Mongo: read the raw event log back and find it.
		var changes = Events().GetAllAsync().ToBlockingEnumerable()
			.OfType<ObjectPropertyChangedEvent>()
			.Where(e => e.TargetId == id && e.PropertyName == nameof(DemoModel.Name))
			.ToArray();

		await Assert.That(changes.Length).IsGreaterThanOrEqualTo(1);
		await Assert.That((string?)changes[^1].NewValue).IsEqualTo("Alice");

		// And no CommandCreatedEvent leaked into the durable log (persistence is off).
		var commandEvents = Events().GetAllAsync().ToBlockingEnumerable().OfType<CommandCreatedEvent>().Count();
		await Assert.That(commandEvents).IsEqualTo(0);
	}

	[Test]
	public async Task Should_replay_state_from_mongo_after_reopen()
	{
		var store = (IObjectStore)Projection();
		var model = new DemoModel();
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);
		model.Name = "Bob";

		// New host + new store against the same Mongo database — must rebuild state by
		// replaying the event log.
		Restart();
		var projection = Projection();
		await projection.LoadStateAsync();

		var store2 = (IObjectStore)projection;
		var reloaded = store2.GetCollection<DemoModel>().FirstOrDefault(m => store2.GetId(m) == id);
		await Assert.That(reloaded).IsNotNull();
		await Assert.That(reloaded!.Name).IsEqualTo("Bob");
	}
}
