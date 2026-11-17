using Microsoft.Extensions.DependencyInjection;
using Synqra.BinarySerializer;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System.Diagnostics;
using TUnit.Assertions.Extensions;
#if NET10_0_OR_GREATER
using Synqra.Replication.AspNetCore;
#endif

namespace Synqra.Tests.Syncronization;

/// <summary>
/// A page left open across a deploy runs the old build until it reloads: the master tells each client that asks which build it
/// runs (<see cref="ServerVersion"/>), on connect and whenever it changes, and can demand a reload. A write the master has not
/// confirmed yet holds the reload back, so a reload never drops one.
/// </summary>
internal class VersionSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;

#if NET10_0_OR_GREATER
	[Test]
	public async Task Should_tell_a_client_the_build_of_its_master()
	{
		var (master, version) = await StartMasterAsync("build-1");
		var client = await StartClientAsync(master);
		try
		{
			var replication = Replication(client);
			await WaitUntilAsync(() => replication.ServerBuild == "build-1", "the client learns the master's build");

			await Assert.That(replication.ReloadRequired).IsFalse().Because("the first build a run hears is its own");
		}
		finally
		{
			await StopAsync(client, master);
		}
	}

	[Test]
	public async Task Should_replicate_on_with_a_master_of_another_build_and_the_same_schemas()
	{
		var (master, version) = await StartMasterAsync("build-1");
		var client = await StartClientAsync(master);
		try
		{
			var replication = Replication(client);
			await WaitUntilAsync(() => replication.ServerBuild == "build-1", "the client learns the master's build");

			version.Announce("build-2"); // a deploy that changed no model: its frames read the same

			await WaitUntilAsync(() => replication.ServerBuild == "build-2", "the client learns the new build");
			await Task.Delay(500);
			await Assert.That(replication.ReloadRequired).IsFalse().Because("the same schemas bundle: the host decides whether to load the new build");
			await Assert.That(replication.Halted).IsFalse();
		}
		finally
		{
			await StopAsync(client, master);
		}
	}

	/// <summary>
	/// A frame names no schema versions, so a client on other schemas than its master's misreads a changed type both know, not only
	/// an unknown one. The master's bundle comes first, and a client on another one applies none of the master's frames: it halts and
	/// asks for a reload before the backlog, a snapshot or a push is read.
	/// </summary>
	[Test]
	public async Task Should_apply_no_frame_of_a_master_that_maps_a_type_at_another_schema_version()
	{
		var (master, version) = await StartMasterAsync("build-1");
		var writer = await StartClientAsync(master);
		var reader = new SynqraTestNode(master.StreamId, builder => builder.Services.AddSbxSerializer(ser => ser.Map(101, 2025.5, typeof(SampleTaskModel)))) { Port = master.Port, RequestSnapshot = false }; // a build where a type both map has another schema
		try
		{
			writer.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "in the backlog" });
			await WaitUntilAsync(() => !Replication(writer).HasUnconfirmedWrites, "the write is in the master's stream");

			await reader.Started;
			var replication = Replication(reader);
			await WaitUntilAsync(() => replication.Halted, "the reader halts at the master's bundle");
			writer.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "pushed later" });
			await Task.Delay(2_000); // a reconnect, the backlog or the push would show by now

			await Assert.That(replication.ReloadRequired).IsTrue();
			await Assert.That(replication.FramesReceived).IsEqualTo(0).Because("no event frame is read before the bundle check passes");
			await Assert.That(reader.StoreContext.GetCollection<SampleTaskModel>().Count).IsEqualTo(0);
			await Assert.That(replication.IsOnline).IsFalse().Because("a halted run opens no connection again");
		}
		finally
		{
			await StopAsync(reader, writer, master);
		}
	}

	[Test]
	public async Task Should_ask_one_named_client_or_every_open_client_to_reload_on_demand()
	{
		var (master, version) = await StartMasterAsync("build-1");
		var clientA = await StartClientAsync(master);
		var clientB = await StartClientAsync(master);
		try
		{
			var a = Replication(clientA);
			var b = Replication(clientB);
			await WaitUntilAsync(() => a.ServerBuild is not null && b.ServerBuild is not null, "both clients learn the master's build");

			version.RequireReload(clientA.Host.Services.GetRequiredService<EventReplicationState>().MyNodeId);
			await WaitUntilAsync(() => a.ReloadRequired, "A, named, is asked to reload");
			await Task.Delay(500);
			await Assert.That(b.ReloadRequired).IsFalse().Because("B was not named");

			version.RequireReload();
			await WaitUntilAsync(() => b.ReloadRequired, "every open client is asked to reload");

			var late = await StartClientAsync(master);
			try
			{
				var c = Replication(late);
				await WaitUntilAsync(() => c.ServerBuild is not null, "a client connecting after the demand learns the master's build");
				await Assert.That(c.ReloadRequired).IsFalse().Because("a page loaded after the demand already runs what it asked for");
			}
			finally
			{
				await StopAsync(late);
			}
		}
		finally
		{
			await StopAsync(clientA, clientB, master);
		}
	}
#endif

	[Test]
	public async Task Should_hold_a_write_unconfirmed_until_the_master_acknowledges_it()
	{
		var streamId = Guid.NewGuid();
		var deaf = new SynqraTestNode(streamId, builder => { }, masterHost: true, acknowledgeInbound: false);
		await deaf.Started;
		var client = new SynqraTestNode(streamId, builder => { }) { Port = deaf.Port, HeartbeatInterval = TimeSpan.Zero };
		await client.Started;
		try
		{
			var replication = Replication(client);
			await WaitUntilAsync(() => replication.IsCaughtUp, "the client is caught up");
			await WaitUntilAsync(() => !replication.HasUnconfirmedWrites, "nothing is unconfirmed before a write");

			client.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "unacknowledged" });

			await WaitUntilAsync(() => replication.HasUnconfirmedWrites, "the write is unconfirmed");
			await Task.Delay(1_500); // sent long since, never acknowledged
			await Assert.That(replication.HasUnconfirmedWrites).IsTrue().Because("a write the master never acknowledged may be lost by a reload");
		}
		finally
		{
			await StopAsync(client, deaf);
		}
	}

	[Test]
	public async Task Should_confirm_a_write_once_the_master_acknowledges_it()
	{
		var streamId = Guid.NewGuid();
		var master = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await master.Started;
		var client = new SynqraTestNode(streamId, builder => { }) { Port = master.Port };
		await client.Started;
		try
		{
			var replication = Replication(client);
			await WaitUntilAsync(() => replication.IsCaughtUp, "the client is caught up");
			var changes = 0;
			var seen = new System.Collections.Concurrent.ConcurrentQueue<bool>();
			replication.StatusChanged += () =>
			{
				changes++;
				seen.Enqueue(replication.HasUnconfirmedWrites);
			};

			client.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "acknowledged" });

			await WaitUntilAsync(() => !replication.HasUnconfirmedWrites, "the master's ack confirms the write");
			await Assert.That(changes).IsGreaterThanOrEqualTo(2).Because($"both turns raised StatusChanged, so a host waiting on them hears the end (seen: {string.Join(",", seen)})");
		}
		finally
		{
			await StopAsync(client, master);
		}
	}

#if NET10_0_OR_GREATER
	async Task<(SynqraTestNode Master, ReplicationVersion Version)> StartMasterAsync(string build)
	{
		var version = new ReplicationVersion(build);
		var master = new SynqraTestNode(Guid.NewGuid(), builder => builder.Services.AddSingleton(version), masterHost: true, useRealEndpoint: true);
		await master.Started;
		return (master, version);
	}

	async Task<SynqraTestNode> StartClientAsync(SynqraTestNode master)
	{
		var client = new SynqraTestNode(master.StreamId, builder => { }) { Port = master.Port };
		await client.Started;
		return client;
	}
#endif

	EventReplicationService Replication(SynqraTestNode node) => node.Host.Services.GetRequiredService<EventReplicationService>();

	async Task WaitUntilAsync(Func<bool> condition, string what)
	{
		var sw = Stopwatch.StartNew();
		while (!condition() && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		await Assert.That(condition()).IsTrue().Because($"timed out waiting until {what}");
	}

	async Task StopAsync(params SynqraTestNode[] nodes)
	{
		foreach (var node in nodes)
		{
			await node.Host.StopAsync();
			await node.Host.DisposeAsync();
		}
	}
}
