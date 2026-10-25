#if NET10_0_OR_GREATER
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.Tests.SampleModels;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// Seeding over the real replication endpoint (docs/model.md §10): the master seeds a new stream before its first subscriber
/// reads it, once however many subscribers race, and a client never seeds a stream it did not create, whatever it has
/// registered, however empty its local log, online or not.
/// </summary>
internal class StreamSeedingSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;

	// Long enough for a client that was going to write something to have written it and sent it.
	const int QuietPeriodMs = 1_500;

	[Test]
	public async Task Should_deliver_the_master_seed_to_the_first_client_of_a_new_stream()
	{
		var streamId = Guid.NewGuid();
		var recorder = new WelcomeRecorder();
		var master = StartMaster(streamId, recorder);
		await master.Started;

		var client = new SynqraTestNode(streamId) { Port = master.Port };
		await client.Started;
		await WaitForOnlineAsync(client);
		await WaitUntilAsync(() => CountWelcomes(client) == 1, "the client holds the seed");
		await Task.Delay(QuietPeriodMs);

		await Assert.That(CountWelcomes(client)).IsEqualTo(1);
		await Assert.That(await CountWelcomesAsync(master)).IsEqualTo(1);
		await Assert.That(recorder.Contexts.Single().Origin).IsEqualTo(StreamOrigin.EmptyOnMaster);
	}

	[Test]
	[Arguments(2)]
	[Arguments(8)]
	public async Task Should_seed_once_when_fresh_clients_connect_at_once(int clients)
	{
		var streamId = Guid.NewGuid();
		var master = StartMaster(streamId, new WelcomeRecorder());
		await master.Started;

		var nodes = Enumerable.Range(0, clients).Select(_ => new SynqraTestNode(streamId) { Port = master.Port }).ToArray();
		await Task.WhenAll(nodes.Select(n => n.Started));
		await Task.WhenAll(nodes.Select(WaitForOnlineAsync));
		foreach (var node in nodes)
		{
			await WaitUntilAsync(() => CountWelcomes(node) == 1, "every client holds the seed");
		}
		await Task.Delay(QuietPeriodMs);

		await Assert.That(await CountWelcomesAsync(master)).IsEqualTo(1);
		foreach (var node in nodes)
		{
			await Assert.That(CountWelcomes(node)).IsEqualTo(1);
		}
	}

	[Test]
	public async Task Should_not_seed_a_stream_the_server_already_wrote_into()
	{
		var streamId = Guid.NewGuid();
		var recorder = new WelcomeRecorder();
		var master = StartMaster(streamId, recorder);
		await master.Started;
		var serverStore = (IObjectStore)await master.Host.Services.GetRequiredService<IProjectionProvider>().GetAsync(streamId);
		serverStore.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "written over REST before anyone connected" });

		var client = new SynqraTestNode(streamId) { Port = master.Port };
		await client.Started;
		await WaitForOnlineAsync(client);
		await WaitUntilAsync(() => client.StoreContext.GetCollection<SampleTaskModel>().Count == 1, "the client holds the server's write");
		await Task.Delay(QuietPeriodMs);

		await Assert.That(CountWelcomes(client)).IsEqualTo(0);
		await Assert.That(await CountWelcomesAsync(master)).IsEqualTo(0);
		await Assert.That(recorder.Contexts).IsEmpty();
	}

	[Test]
	public async Task Should_never_seed_on_a_client_with_seeders_registered_and_an_empty_log()
	{
		var streamId = Guid.NewGuid();
		var master = new SynqraTestNode(streamId, masterHost: true, useRealEndpoint: true); // no seeder on the master
		await master.Started;
		var clientRecorder = new WelcomeRecorder();

		var client = new SynqraTestNode(streamId, builder => AddWelcomeSeeder(builder, clientRecorder)) { Port = master.Port };
		await client.Started;
		await WaitForOnlineAsync(client);
		await Task.Delay(QuietPeriodMs);

		await Assert.That(clientRecorder.Contexts).IsEmpty();
		await Assert.That(CountWelcomes(client)).IsEqualTo(0);
		await Assert.That(await CountEventsAsync(client)).IsEqualTo(0);
		await Assert.That(await CountEventsAsync(master)).IsEqualTo(0);
	}

	[Test]
	public async Task Should_never_seed_on_a_client_that_cannot_reach_its_master()
	{
		var streamId = Guid.NewGuid();
		var clientRecorder = new WelcomeRecorder();

		var client = new SynqraTestNode(streamId, builder => AddWelcomeSeeder(builder, clientRecorder)) { Port = FindFreePort() }; // nothing listens there
		await client.Started;
		await Task.Delay(QuietPeriodMs);

		await Assert.That(client.StoreContext.IsOnline()).IsFalse();
		await Assert.That(clientRecorder.Contexts).IsEmpty();
		await Assert.That(CountWelcomes(client)).IsEqualTo(0);
		await Assert.That(await CountEventsAsync(client)).IsEqualTo(0);
	}

	[Test]
	[Arguments(1)]
	[Arguments(5)]
	public async Task Should_not_seed_again_when_a_client_comes_back_with_an_empty_log(int comebacks)
	{
		var streamId = Guid.NewGuid();
		var recorder = new WelcomeRecorder();
		var master = StartMaster(streamId, recorder);
		await master.Started;
		var client = new SynqraTestNode(streamId) { Port = master.Port };
		await client.Started;
		await WaitForOnlineAsync(client);
		await WaitUntilAsync(() => CountWelcomes(client) == 1, "the client holds the seed");

		for (var i = 0; i < comebacks; i++)
		{
			// A Force Resync, a cleared browser, a new device: the run ends, and the next starts with an empty log and a cold cursor.
			await client.Host.StopAsync();
			await ((IAsyncDisposable)client.Host).DisposeAsync();
			client = new SynqraTestNode(streamId, builder => AddWelcomeSeeder(builder, new WelcomeRecorder())) { Port = master.Port };
			await client.Started;
			await WaitForOnlineAsync(client);
			await WaitUntilAsync(() => CountWelcomes(client) == 1, "the client that came back holds the seed");
		}
		await Task.Delay(QuietPeriodMs);

		await Assert.That(CountWelcomes(client)).IsEqualTo(1);
		await Assert.That(await CountWelcomesAsync(master)).IsEqualTo(1);
		await Assert.That(recorder.Contexts.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_not_seed_again_when_a_second_master_process_serves_the_same_log()
	{
		var streamId = Guid.NewGuid();
		var master = StartMaster(streamId, new WelcomeRecorder());
		await master.Started;
		var client = new SynqraTestNode(streamId) { Port = master.Port };
		await client.Started;
		await WaitForOnlineAsync(client);
		await WaitUntilAsync(() => CountWelcomes(client) == 1, "the client holds the seed");

		// The restarted master: nothing settled in its memory, the log it reads is the one that holds the seed.
		var restarted = new StreamSeeding([new WelcomeSeeder(new WelcomeRecorder())]);
		var store = (IObjectStore)await master.Host.Services.GetRequiredService<IProjectionProvider>().GetAsync(streamId);
		var seeded = await restarted.SeedIfEmptyAsync(streamId, master.Events, store);

		await Assert.That(seeded).IsFalse();
		await Assert.That(await CountWelcomesAsync(master)).IsEqualTo(1);
	}

	/// <summary>
	/// The creator's seed, made offline, reaches the master once its creator connects, and every later client gets that one seed.
	/// The master's own seeder claims only streams it was asked to seed as their creator (<see cref="StreamOrigin"/>), so it does
	/// not seed again a stream whose creator is a client.
	/// </summary>
	[Test]
	public async Task Should_carry_a_seed_made_offline_by_the_streams_creator_to_every_later_client()
	{
		var streamId = Guid.NewGuid();
		var port = FindFreePort();
		var creatorRecorder = new WelcomeRecorder();
		var creator = new SynqraTestNode(streamId, builder => AddWelcomeSeeder(builder, creatorRecorder)) { Port = port };
		await creator.Started;
		await creator.Host.Services.GetRequiredService<StreamSeeding>().SeedCreatedAsync(streamId, creator.Events, creator.StoreContext);
		await Assert.That(creator.StoreContext.IsOnline()).IsFalse();
		await Assert.That(CountWelcomes(creator)).IsEqualTo(1);

		var masterRecorder = new WelcomeRecorder();
		var master = new SynqraTestNode(streamId, builder =>
		{
			builder.Configuration["URLS"] = $"http://127.0.0.1:{port}";
			AddWelcomeSeeder(builder, masterRecorder, StreamOrigin.CreatedHere); // a seeder for client-created streams: the master never claims them
		}, masterHost: true, useRealEndpoint: true);
		await master.Started;
		await WaitForOnlineAsync(creator);
		await WaitUntilAsync(async () => await CountWelcomesAsync(master) == 1, "the creator's seed reaches the master");

		var later = new SynqraTestNode(streamId) { Port = port };
		await later.Started;
		await WaitForOnlineAsync(later);
		await WaitUntilAsync(() => CountWelcomes(later) == 1, "a later client holds the creator's seed");
		await Task.Delay(QuietPeriodMs);

		await Assert.That(masterRecorder.Contexts).IsEmpty();
		await Assert.That(creatorRecorder.Contexts.Single().Origin).IsEqualTo(StreamOrigin.CreatedHere);
		await Assert.That(await CountWelcomesAsync(master)).IsEqualTo(1);
		await Assert.That(CountWelcomes(later)).IsEqualTo(1);
		await Assert.That(CountWelcomes(creator)).IsEqualTo(1);
	}

	/// <summary>
	/// A deploy that evolves the seed: the next master process, over the same log, brings the stream's seed to its new version for
	/// its first subscriber, once; later subscribers and later processes leave it.
	/// </summary>
	[Test]
	public async Task Should_evolve_a_seed_for_the_first_subscriber_of_the_next_master_process()
	{
		var streamId = Guid.NewGuid();
		var log = new SharedLog();
		var before = new SynqraTestNode(streamId, builder => AddVersionedMaster(builder, log, 1), masterHost: true, useRealEndpoint: true);
		await before.Started;
		var early = new SynqraTestNode(streamId) { Port = before.Port };
		await early.Started;
		await WaitForOnlineAsync(early);
		await WaitUntilAsync(() => CountMarkers(early, 1) == 1, "the first client holds the version 1 seed");

		var after = new SynqraTestNode(streamId, builder => AddVersionedMaster(builder, log, 2), masterHost: true, useRealEndpoint: true); // the new build
		await after.Started;
		var later = new SynqraTestNode(streamId) { Port = after.Port };
		await later.Started;
		await WaitForOnlineAsync(later);
		await WaitUntilAsync(() => CountMarkers(later, 2) == 1, "the next client holds the evolved seed");
		var again = new SynqraTestNode(streamId) { Port = after.Port };
		await again.Started;
		await WaitForOnlineAsync(again);
		await WaitUntilAsync(() => CountMarkers(again, 2) == 1, "a third client holds it too");
		await Task.Delay(QuietPeriodMs);

		await Assert.That(CountMarkers(later, 1)).IsEqualTo(1);
		await Assert.That(CountMarkers(again, 2)).IsEqualTo(1);
		await Assert.That(await CountMarkersAsync(after, 1)).IsEqualTo(1);
		await Assert.That(await CountMarkersAsync(after, 2)).IsEqualTo(1);
	}

	/// <summary>The host's guard refuses a peer's event: the sender is told, the master's log never holds it; other events pass.</summary>
	[Test]
	public async Task Should_refuse_a_peers_event_a_guard_forbids_and_let_the_others_through()
	{
		var streamId = Guid.NewGuid();
		var guard = new SubjectGuard("locked");
		var master = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IInboundEventGuard>(guard), masterHost: true, useRealEndpoint: true);
		await master.Started;
		var client = new SynqraTestNode(streamId) { Port = master.Port };
		await client.Started;
		await WaitForOnlineAsync(client);

		client.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "locked" });
		client.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "free" });
		await WaitUntilAsync(async () => await CountSubjectsAsync(master, "free") == 1, "the allowed write reaches the master");
		await Task.Delay(QuietPeriodMs);

		await Assert.That(await CountSubjectsAsync(master, "locked")).IsEqualTo(0);
		await Assert.That(guard.Refused).IsGreaterThan(0);
	}

	void AddVersionedMaster(IHostApplicationBuilder builder, SharedLog log, int version)
	{
		builder.Services.AddSingleton<IAppendStorage<Event, (Guid, Guid)>>(log.Storage); // one log across both master processes
		builder.Services.AddSingleton(new MarkerSeeder(version));
		builder.Services.AddSingleton<IStreamSeeder>(sp => sp.GetRequiredService<MarkerSeeder>());
		builder.Services.AddSingleton<StreamSeeding>();
	}

	int CountMarkers(SynqraTestNode node, int version) => node.StoreContext.GetCollection<SampleTaskModel>().Count(t => t.Subject == "version:" + version);

	Task<int> CountMarkersAsync(SynqraTestNode node, int version) => CountSubjectsAsync(node, "version:" + version);

	async Task<int> CountSubjectsAsync(SynqraTestNode node, string subject)
	{
		var count = 0;
		await foreach (var ev in node.Events.GetAllAsync())
		{
			if (ev is ComponentAddedEvent { Data: SampleTaskModel { Subject: var s } } && s == subject)
			{
				count++;
			}
		}
		return count;
	}

	/// <summary>
	/// One event log two master processes share, as a deploy shares the database: events are kept serialized and read back as new
	/// objects, their stream and sequence restored from the key, so neither process sees the other's live instances.
	/// </summary>
	public sealed class SharedLog
	{
		public IAppendStorage<Event, (Guid, Guid)> Storage { get; } = new SerializedStorage();

		sealed class SerializedStorage : IAppendStorage<Event, (Guid, Guid)>
		{
			readonly SortedDictionary<(Guid Stream, Guid Sequence), string> _rows = new(Comparer<(Guid Stream, Guid Sequence)>.Create((a, b) => a.Stream.CompareBigEndian(b.Stream) is var first && first != 0 ? first : a.Sequence.CompareBigEndian(b.Sequence)));
			readonly System.Text.Json.JsonSerializerOptions _json = SampleJsonSerializerContext.DefaultOptions;

			public Task AppendAsync(Event item, CancellationToken cancellationToken = default)
			{
				lock (_rows)
				{
					_rows.Add((item.StreamId, item.SequenceId), System.Text.Json.JsonSerializer.Serialize(item, _json));
				}
				return Task.CompletedTask;
			}

			public Task<Event> GetAsync((Guid, Guid) key, CancellationToken cancellationToken = default)
			{
				string row;
				lock (_rows)
				{
					row = _rows[key];
				}
				return Task.FromResult(Read(key, row));
			}

			public async IAsyncEnumerable<Event> GetAllAsync((Guid, Guid) from = default, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
			{
				KeyValuePair<(Guid Stream, Guid Sequence), string>[] rows;
				lock (_rows)
				{
					rows = _rows.ToArray();
				}
				var hasCursor = from != default;
				foreach (var row in rows.SkipWhile(r => hasCursor && _rows.Comparer.Compare(r.Key, from) <= 0))
				{
					yield return Read(row.Key, row.Value);
				}
				await Task.CompletedTask;
			}

			Event Read((Guid Stream, Guid Sequence) key, string row)
			{
				var ev = System.Text.Json.JsonSerializer.Deserialize<Event>(row, _json)!;
				ev.StreamId = key.Stream;
				ev.SequenceId = key.Sequence;
				return ev;
			}

			public void Dispose()
			{
			}

			public ValueTask DisposeAsync() => ValueTask.CompletedTask;
		}
	}

	/// <summary>Seeds a "version:N" task; reads the highest version seeded; an upgrade adds the task of the version it brings.</summary>
	public sealed class MarkerSeeder(int version) : IStreamSeeder
	{
		public int Version => version;

		public Task SeedAsync(StreamSeedContext context, IObjectStore store) => AddMarkerAsync(store, version);

		public Task<int?> ReadVersionAsync(StreamSeedContext context, IObjectStore store)
		{
			var versions = store.GetCollection<SampleTaskModel>().Select(t => t.Subject).Where(s => s?.StartsWith("version:", StringComparison.Ordinal) == true).Select(s => int.Parse(s!["version:".Length..])).ToList();
			return Task.FromResult<int?>(versions.Count == 0 ? null : versions.Max());
		}

		public Task UpgradeAsync(StreamSeedContext context, IObjectStore store, int fromVersion) => AddMarkerAsync(store, version);

		Task AddMarkerAsync(IObjectStore store, int markerVersion)
		{
			store.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "version:" + markerVersion });
			return Task.CompletedTask;
		}
	}

	/// <summary>Refuses any event that adds a task with the locked subject, as a host refuses edits to its system objects.</summary>
	public sealed class SubjectGuard(string locked) : IInboundEventGuard
	{
		int _refused;

		public int Refused => _refused;

		public ValueTask<string?> RefuseAsync(Event ev, CancellationToken cancellationToken = default)
		{
			string? reason = null;
			if (ev is ComponentAddedEvent { Data: SampleTaskModel { Subject: var subject } } && subject == locked)
			{
				Interlocked.Increment(ref _refused);
				reason = $"'{locked}' is not for a peer to write";
			}
			return ValueTask.FromResult(reason);
		}
	}

	SynqraTestNode StartMaster(Guid streamId, WelcomeRecorder recorder)
		=> new(streamId, builder => AddWelcomeSeeder(builder, recorder), masterHost: true, useRealEndpoint: true);

	void AddWelcomeSeeder(IHostApplicationBuilder builder, WelcomeRecorder recorder, StreamOrigin? only = null)
	{
		builder.Services.AddSingleton(recorder);
		builder.Services.AddSingleton(new WelcomeSeederOptions { Only = only });
		builder.Services.AddStreamSeeder<WelcomeSeeder>();
	}

	int CountWelcomes(SynqraTestNode node) => node.StoreContext.GetCollection<SampleTaskModel>().Count(t => t.Subject == WelcomeSeeder.Subject);

	async Task<int> CountWelcomesAsync(SynqraTestNode node)
	{
		var count = 0;
		await foreach (var ev in node.Events.GetAllAsync())
		{
			if (ev is ComponentAddedEvent { Data: SampleTaskModel { Subject: WelcomeSeeder.Subject } })
			{
				count++;
			}
		}
		return count;
	}

	async Task<int> CountEventsAsync(SynqraTestNode node)
	{
		var count = 0;
		await foreach (var _ in node.Events.GetAllAsync())
		{
			count++;
		}
		return count;
	}

	ushort FindFreePort()
	{
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		var port = checked((ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
		listener.Stop();
		return port;
	}

	Task WaitUntilAsync(Func<bool> condition, string what) => WaitUntilAsync(() => Task.FromResult(condition()), what);

	async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
	{
		var sw = Stopwatch.StartNew();
		while (!await condition() && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		await Assert.That(await condition()).IsTrue().Because($"timed out waiting until {what}");
	}

	async Task WaitForOnlineAsync(SynqraTestNode node)
	{
		var sw = Stopwatch.StartNew();
		while (!node.StoreContext.IsOnline())
		{
			if (sw.ElapsedMilliseconds > PropagationTimeoutMs && !Debugger.IsAttached)
			{
				throw new TimeoutException("Node did not come online within 30s");
			}
			await Task.Delay(100);
		}
	}

	public sealed class WelcomeRecorder
	{
		public ConcurrentQueue<StreamSeedContext> Contexts { get; } = new();
	}

	public sealed class WelcomeSeederOptions
	{
		/// <summary>The one origin this seeder serves; null serves both.</summary>
		public StreamOrigin? Only { get; init; }
	}

	/// <summary>Writes the one "Welcome" task a brand-new stream starts with.</summary>
	public sealed class WelcomeSeeder(WelcomeRecorder recorder, WelcomeSeederOptions? options = null) : IStreamSeeder
	{
		public const string Subject = "Welcome";

		public async Task SeedAsync(StreamSeedContext context, IObjectStore store)
		{
			if (options?.Only is not StreamOrigin only || only == context.Origin)
			{
				recorder.Contexts.Enqueue(context);
				await Task.Yield(); // a seed that takes its time widens any race between subscribers
				store.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = Subject });
			}
		}
	}
}
#endif
