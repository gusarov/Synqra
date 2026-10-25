using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.AppendStorage.InMemory;
using Synqra.Projection.InMemory;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// A stream is seeded only when it is proven new (docs/model.md §10): by its creator, or by the master that finds its log
/// empty. Never twice, never on a stream that holds history, never by a seeder that fails halfway and is retried.
/// </summary>
public class StreamSeedingTests : TestsStateManagement
{
	readonly SeedRecorder _recorder = new();

	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		// The master's log over an ordered, locked storage, like every real one: the fake list of the base keeps arrival order only.
		hostApplicationBuilder.Services.RemoveAll<IAppendStorage<Event, (Guid, Guid)>>();
		hostApplicationBuilder.Services.RemoveAll<IEventLog>();
		hostApplicationBuilder.Services.AddAppendStorageInMemory<Event, (Guid, Guid)>(x => (x.StreamId, x.SequenceId));
		hostApplicationBuilder.Services.AddServerEventLog();
		hostApplicationBuilder.Services.AddInMemorySynqraStore();
		hostApplicationBuilder.Services.AddSingleton(_recorder);
		hostApplicationBuilder.Services.AddStreamSeeder<RecordingSeeder>();
	}

	IEventLog Log => ServiceProvider.GetRequiredService<IEventLog>();

	StreamSeeding Seeding => ServiceProvider.GetRequiredService<StreamSeeding>();

	async Task<IObjectStore> OpenStoreAsync(Guid stream) => (IObjectStore)await ServiceProvider.GetRequiredService<IProjectionProvider>().GetAsync(stream);

	async Task<int> CountSeedsAsync(Guid stream)
	{
		var count = 0;
		await foreach (var ev in Log.ReadStreamAsync(stream, default))
		{
			if (ev is ComponentAddedEvent { Data: TestComponentNode { Name: RecordingSeeder.SeedName } })
			{
				count++;
			}
		}
		return count;
	}

	[Test]
	public async Task Should_seed_an_empty_stream_on_the_master_once()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);

		var first = await Seeding.SeedIfEmptyAsync(stream, Log, store);
		var second = await Seeding.SeedIfEmptyAsync(stream, Log, store);

		await Assert.That(first).IsTrue();
		await Assert.That(second).IsFalse();
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(1);
		await Assert.That(store.GetCollection<TestComponentNode>().Count(n => n.Name == RecordingSeeder.SeedName)).IsEqualTo(1);
		await Assert.That(_recorder.Contexts.Single().Origin).IsEqualTo(StreamOrigin.EmptyOnMaster);
		await Assert.That(_recorder.Contexts.Single().StreamId).IsEqualTo(stream);
	}

	[Test]
	public async Task Should_not_seed_a_stream_that_holds_events()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		await WriteNodeAsync(store, "existing history");

		var seeded = await Seeding.SeedIfEmptyAsync(stream, Log, store);

		await Assert.That(seeded).IsFalse();
		await Assert.That(_recorder.Contexts).IsEmpty();
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(0);
	}

	[Test]
	public async Task Should_not_seed_again_in_a_new_process_over_the_same_log()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		await Seeding.SeedIfEmptyAsync(stream, Log, store);

		var restarted = new StreamSeeding([new RecordingSeeder(_recorder)]); // nothing settled in memory: only the log can say
		var seeded = await restarted.SeedIfEmptyAsync(stream, Log, store);

		await Assert.That(seeded).IsFalse();
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(1);
	}

	[Test]
	[Arguments(2)]
	[Arguments(16)]
	[Arguments(64)]
	public async Task Should_seed_once_when_many_subscribers_race_for_a_new_stream(int callers)
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		var (seeding, log) = (Seeding, Log); // the test host builds lazily, and not for racing callers
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var racers = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
		{
			await start.Task;
			return await seeding.SeedIfEmptyAsync(stream, log, store);
		})).ToArray();
		start.SetResult(); // every racer is waiting: they all start at once

		var results = await Task.WhenAll(racers);

		await Assert.That(results.Count(r => r)).IsEqualTo(1);
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(1);
		await Assert.That(_recorder.Contexts.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_seed_each_new_stream_once_when_many_streams_race()
	{
		var streams = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
		var stores = new Dictionary<Guid, IObjectStore>();
		foreach (var stream in streams)
		{
			stores[stream] = await OpenStoreAsync(stream);
		}

		var (seeding, log) = (Seeding, Log);
		await Task.WhenAll(streams.SelectMany(stream => Enumerable.Range(0, 4).Select(_ => Task.Run(() => seeding.SeedIfEmptyAsync(stream, log, stores[stream])))));

		foreach (var stream in streams)
		{
			await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(1);
		}
	}

	[Test]
	public async Task Should_run_every_seeder_in_registration_order()
	{
		var order = new List<string>();
		var seeding = new StreamSeeding([new DelegateSeeder(_ => order.Add("first")), new DelegateSeeder(_ => order.Add("second"))]);
		var stream = Guid.NewGuid();

		await seeding.SeedIfEmptyAsync(stream, Log, await OpenStoreAsync(stream));

		await Assert.That(order).IsEquivalentTo(["first", "second"]);
	}

	[Test]
	public async Task Should_let_a_seeder_write_nothing_for_a_stream_it_does_not_own()
	{
		var owned = Guid.NewGuid();
		var seeder = new RecordingSeeder(_recorder) { Owns = stream => stream == owned };
		var seeding = new StreamSeeding([seeder]);
		var other = Guid.NewGuid();

		await seeding.SeedIfEmptyAsync(other, Log, await OpenStoreAsync(other));
		await seeding.SeedIfEmptyAsync(owned, Log, await OpenStoreAsync(owned));

		await Assert.That(await CountSeedsAsync(other)).IsEqualTo(0);
		await Assert.That(await CountSeedsAsync(owned)).IsEqualTo(1);
	}

	[Test]
	public async Task Should_read_nothing_without_seeders()
	{
		var seeding = new StreamSeeding([]);
		var stream = Guid.NewGuid();

		var seeded = await seeding.SeedIfEmptyAsync(stream, new UnreadableLog(), await OpenStoreAsync(stream));

		await Assert.That(seeding.HasSeeders).IsFalse();
		await Assert.That(seeded).IsFalse();
	}

	[Test]
	public async Task Should_wrap_a_failing_seeder_and_seed_on_the_next_try_when_it_committed_nothing()
	{
		var failures = 1;
		var seeding = new StreamSeeding([new DelegateSeeder(_ =>
		{
			if (failures-- > 0)
			{
				throw new InvalidOperationException("transient");
			}
		}), new RecordingSeeder(_recorder)]);
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);

		var failure = await Assert.ThrowsAsync<StreamSeedingException>(() => seeding.SeedIfEmptyAsync(stream, Log, store));
		var retried = await seeding.SeedIfEmptyAsync(stream, Log, store);

		await Assert.That(failure!.InnerException).IsTypeOf<InvalidOperationException>();
		await Assert.That(failure.Message).Contains(stream.ToString());
		await Assert.That(retried).IsTrue();
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(1);
	}

	[Test]
	public async Task Should_never_seed_again_a_stream_a_failed_seeder_wrote_into()
	{
		var seeding = new StreamSeeding([new RecordingSeeder(_recorder), new DelegateSeeder(_ => throw new InvalidOperationException("after the first write"))]);
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);

		await Assert.ThrowsAsync<StreamSeedingException>(() => seeding.SeedIfEmptyAsync(stream, Log, store));
		var again = await new StreamSeeding([new RecordingSeeder(_recorder)]).SeedIfEmptyAsync(stream, Log, store);

		await Assert.That(again).IsFalse();
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(1);
	}

	[Test]
	public async Task Should_seed_a_stream_its_creator_made_without_any_server()
	{
		var created = Guid.NewGuid(); // minted here, offline: no replication is registered in this host
		var store = await OpenStoreAsync(created);

		await Seeding.SeedCreatedAsync(created, Log, store);

		await Assert.That(await CountSeedsAsync(created)).IsEqualTo(1);
		await Assert.That(_recorder.Contexts.Single().Origin).IsEqualTo(StreamOrigin.CreatedHere);
		await Assert.That(ServiceProvider.GetService<IEventReplicationService>()).IsNull();
	}

	[Test]
	public async Task Should_refuse_a_creator_seed_for_a_stream_that_holds_events()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		await WriteNodeAsync(store, "existing history");

		await Assert.ThrowsAsync<StreamNotNewException>(() => Seeding.SeedCreatedAsync(stream, Log, store));
		await Assert.That(_recorder.Contexts).IsEmpty();
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(0);
	}

	[Test]
	public async Task Should_refuse_a_second_creator_seed()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		await Seeding.SeedCreatedAsync(stream, Log, store);

		await Assert.ThrowsAsync<StreamNotNewException>(() => Seeding.SeedCreatedAsync(stream, Log, store));
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(1);
	}

	[Test]
	public async Task Should_not_let_the_master_seed_a_stream_its_creator_seeded()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		await Seeding.SeedCreatedAsync(stream, Log, store);

		var seeded = await new StreamSeeding([new RecordingSeeder(_recorder)]).SeedIfEmptyAsync(stream, Log, store);

		await Assert.That(seeded).IsFalse();
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(1);
	}

	[Test]
	public async Task Should_stop_waiting_when_cancelled()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		using var cancelled = new CancellationTokenSource();
		cancelled.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(() => Seeding.SeedIfEmptyAsync(stream, Log, store, cancelled.Token));
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(0);
	}

	[Test]
	public async Task Should_register_seeders_per_service_key()
	{
		var services = new ServiceCollection();
		services.AddSingleton(_recorder);
		services.AddStreamSeeder<RecordingSeeder>("A");
		services.AddStreamSeeder<RecordingSeeder>("A");
		services.AddStreamSeeder<RecordingSeeder>();
		using var provider = services.BuildServiceProvider();

		await Assert.That(provider.GetRequiredKeyedService<StreamSeeding>("A").HasSeeders).IsTrue();
		await Assert.That(provider.GetKeyedServices<IStreamSeeder>("A").Count()).IsEqualTo(2);
		await Assert.That(provider.GetServices<IStreamSeeder>().Count()).IsEqualTo(1);
		await Assert.That(provider.GetKeyedService<StreamSeeding>("B")).IsNull();
	}

	// ---------------------------------------------------------------- evolving a seed

	[Test]
	public async Task Should_seed_an_empty_stream_at_the_current_version()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		var seeding = new StreamSeeding([new VersionedSeeder(2)]);

		var outcome = await seeding.SeedOrUpgradeAsync(stream, Log, store);

		await Assert.That(outcome).IsEqualTo(StreamSeedOutcome.Seeded);
		await Assert.That(ReadMarkers(store)).IsEquivalentTo([2]);
	}

	[Test]
	public async Task Should_evolve_an_older_seed_once_and_not_again_after_a_restart()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		await new StreamSeeding([new VersionedSeeder(1)]).SeedOrUpgradeAsync(stream, Log, store); // seeded by the build before

		var upgraded = await new StreamSeeding([new VersionedSeeder(3)]).SeedOrUpgradeAsync(stream, Log, store);
		var restarted = await new StreamSeeding([new VersionedSeeder(3)]).SeedOrUpgradeAsync(stream, Log, store);

		await Assert.That(upgraded).IsEqualTo(StreamSeedOutcome.Upgraded);
		await Assert.That(restarted).IsEqualTo(StreamSeedOutcome.None);
		await Assert.That(ReadMarkers(store)).IsEquivalentTo([1, 3]);
	}

	[Test]
	public async Task Should_evolve_once_when_many_subscribers_race()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		var log = Log;
		await new StreamSeeding([new VersionedSeeder(1)]).SeedOrUpgradeAsync(stream, log, store);
		var seeding = new StreamSeeding([new VersionedSeeder(2)]);
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var racers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
		{
			await start.Task;
			return await seeding.SeedOrUpgradeAsync(stream, log, store);
		})).ToArray();
		start.SetResult();

		var outcomes = await Task.WhenAll(racers);

		await Assert.That(outcomes.Count(o => o == StreamSeedOutcome.Upgraded)).IsEqualTo(1);
		await Assert.That(ReadMarkers(store)).IsEquivalentTo([1, 2]);
	}

	[Test]
	public async Task Should_leave_alone_a_stream_whose_seed_the_seeder_does_not_find()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		await WriteNodeAsync(store, "history someone else wrote");

		var outcome = await new StreamSeeding([new VersionedSeeder(5)]).SeedOrUpgradeAsync(stream, Log, store);

		await Assert.That(outcome).IsEqualTo(StreamSeedOutcome.None);
		await Assert.That(ReadMarkers(store)).IsEmpty();
	}

	[Test]
	public async Task Should_wrap_a_failing_upgrade_and_try_it_again_on_the_next_subscriber()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		await new StreamSeeding([new VersionedSeeder(1)]).SeedOrUpgradeAsync(stream, Log, store);
		var seeding = new StreamSeeding([new VersionedSeeder(2) { FailuresLeft = 1 }]);

		var failure = await Assert.ThrowsAsync<StreamSeedingException>(() => seeding.SeedOrUpgradeAsync(stream, Log, store));
		var retried = await seeding.SeedOrUpgradeAsync(stream, Log, store);

		await Assert.That(failure!.Message).Contains("version 2");
		await Assert.That(retried).IsEqualTo(StreamSeedOutcome.Upgraded);
		await Assert.That(ReadMarkers(store)).IsEquivalentTo([1, 2]);
	}

	[Test]
	public async Task Should_not_evolve_from_a_seeder_that_does_not_version_its_seed()
	{
		var stream = Guid.NewGuid();
		var store = await OpenStoreAsync(stream);
		await Seeding.SeedIfEmptyAsync(stream, Log, store);

		var outcome = await new StreamSeeding([new RecordingSeeder(_recorder)]).SeedOrUpgradeAsync(stream, Log, store);

		await Assert.That(outcome).IsEqualTo(StreamSeedOutcome.None);
		await Assert.That(await CountSeedsAsync(stream)).IsEqualTo(1);
	}

	/// <summary>The version markers a <see cref="VersionedSeeder"/> wrote into the stream.</summary>
	List<int> ReadMarkers(IObjectStore store) => new VersionedSeeder(0).ReadMarkers(store);

	/// <summary>Seeds a "version:N" marker node at its version; an upgrade adds the marker of the version it brings the stream to.</summary>
	public sealed class VersionedSeeder(int version) : IStreamSeeder
	{
		public int FailuresLeft { get; set; }

		public int Version => version;

		public Task SeedAsync(StreamSeedContext context, IObjectStore store) => AddMarkerAsync(store, version);

		public Task<int?> ReadVersionAsync(StreamSeedContext context, IObjectStore store)
		{
			var markers = ReadMarkers(store);
			return Task.FromResult<int?>(markers.Count == 0 ? null : markers.Max());
		}

		public Task UpgradeAsync(StreamSeedContext context, IObjectStore store, int fromVersion)
		{
			if (FailuresLeft-- > 0)
			{
				throw new InvalidOperationException($"upgrade from {fromVersion} failed");
			}
			return AddMarkerAsync(store, version);
		}

		public List<int> ReadMarkers(IObjectStore store) => store.GetCollection<TestComponentNode>()
			.Select(n => n.Name)
			.Where(n => n?.StartsWith("version:", StringComparison.Ordinal) == true)
			.Select(n => int.Parse(n!["version:".Length..]))
			.ToList();

		async Task AddMarkerAsync(IObjectStore store, int markerVersion)
		{
			var node = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));
			var id = Guid.NewGuid();
			await store.SubmitCommandAsync(new AddComponentCommand
			{
				TargetTypeId = node.TypeId,
				CollectionId = node.CollectionId,
				TargetId = id,
				ComponentTypeId = node.TypeId,
				ComponentId = id,
				Data = new TestComponentNode { Name = "version:" + markerVersion },
			});
		}
	}

	async Task WriteNodeAsync(IObjectStore store, string name)
	{
		var node = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));
		var id = Guid.NewGuid();
		await store.SubmitCommandAsync(new AddComponentCommand
		{
			TargetTypeId = node.TypeId,
			CollectionId = node.CollectionId,
			TargetId = id,
			ComponentTypeId = node.TypeId,
			ComponentId = id,
			Data = new TestComponentNode { Name = name },
		});
	}

	/// <summary>What the seeders of one test were asked to seed.</summary>
	public sealed class SeedRecorder
	{
		public ConcurrentQueue<StreamSeedContext> Contexts { get; } = new();
	}

	/// <summary>Writes one node named <see cref="SeedName"/> into each stream it owns, as one command.</summary>
	public sealed class RecordingSeeder(SeedRecorder recorder) : IStreamSeeder
	{
		public const string SeedName = "seed";

		public Func<Guid, bool> Owns { get; init; } = _ => true;

		public async Task SeedAsync(StreamSeedContext context, IObjectStore store)
		{
			if (Owns(context.StreamId))
			{
				recorder.Contexts.Enqueue(context);
				var node = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));
				var id = Guid.NewGuid();
				await Task.Yield(); // a seed that takes its time widens any race between callers
				await store.SubmitCommandAsync(new AddComponentCommand
				{
					TargetTypeId = node.TypeId,
					CollectionId = node.CollectionId,
					TargetId = id,
					ComponentTypeId = node.TypeId,
					ComponentId = id,
					Data = new TestComponentNode { Name = SeedName },
				});
			}
		}
	}

	sealed class DelegateSeeder(Action<StreamSeedContext> seed) : IStreamSeeder
	{
		public Task SeedAsync(StreamSeedContext context, IObjectStore store)
		{
			seed(context);
			return Task.CompletedTask;
		}
	}

	/// <summary>A log that fails any read: with no seeder, seeding must not touch the log at all.</summary>
	sealed class UnreadableLog : IEventLog
	{
		public Task AppendAsync(Event item, CancellationToken cancellationToken = default) => throw new InvalidOperationException("unexpected write");

		public Task<Event> GetAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("unexpected read");

		public IAsyncEnumerable<Event> GetAllAsync((Guid Stream, Guid Sequence) from = default, CancellationToken cancellationToken = default) => throw new InvalidOperationException("unexpected read");

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
