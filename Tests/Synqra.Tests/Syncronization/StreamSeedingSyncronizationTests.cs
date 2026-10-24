#if NET10_0_OR_GREATER
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
