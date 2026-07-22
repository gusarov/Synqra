#if NET10_0_OR_GREATER
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// End-to-end socket-level proof of the WS replication endpoint's stream isolation, over the REAL
/// production endpoint (<see cref="Synqra.Replication.AspNetCore.SynqraReplicationEndpointExtensions"/>),
/// not the hand-rolled simulator. The master is a real-endpoint host whose per-connection stream comes
/// from a <c>?stream=</c> query (the test's stand-in for Quotaly's auth middleware); clients are real
/// <see cref="EventReplicationService"/> nodes. These guard what <see cref="ReplicationStreamScopeTests"/>
/// asserts on the pure rule: that the endpoint actually wires that rule into both the live-broadcast and
/// backlog-replay paths, so a client on stream A never sees stream B's data on the wire.
/// net10-only — the production endpoint targets net10.
/// </summary>
[NotInParallel]
internal class ReplicationEndpointIsolationTests : BaseTest
{
	// Matches the 30s online-wait budget (see WaitForOnlineAsync): under docker CPU contention a
	// node can spend most of that budget just connecting, so a tighter propagation window
	// occasionally lapses under load even though no event is dropped. Polling exits on first
	// success, so the happy path never waits the full budget.
	const int PropagationTimeoutMs = 30_000;
	// After the same-stream peer has demonstrably received an event, a cross-stream peer either
	// already has it (a leak) or never will — the endpoint fans a single event out to all sockets in
	// one guarded pass. This grace just lets any erroneous delivery land before we assert its absence.
	const int LeakGraceMs = 750;

	[Test]
	public async Task Live_broadcast_is_not_delivered_across_streams()
	{
		var streamA = Guid.NewGuid();
		var streamB = Guid.NewGuid();

		var master = new SynqraTestNode(Guid.NewGuid(), builder => { }, masterHost: true, useRealEndpoint: true);
		await master.Started;

		// A same-stream peer (A2) and a different-stream peer (B) are both connected and online BEFORE
		// the write, so both are eligible to receive the broadcast — only the stream match should gate it.
		var nodeA2 = new SynqraTestNode(streamA, builder => { }) { Port = master.Port };
		var nodeB = new SynqraTestNode(streamB, builder => { }) { Port = master.Port };
		await nodeA2.Started;
		await nodeB.Started;
		await WaitForOnlineAsync(nodeA2);
		await WaitForOnlineAsync(nodeB);

		var nodeA = new SynqraTestNode(streamA, builder => { }) { Port = master.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "A's private task" });

		// Positive: the same-stream peer converges.
		var a2Got = await WaitForCountAsync(nodeA2, 1, PropagationTimeoutMs);
		await Assert.That(a2Got).IsTrue();
		await Assert.That(nodeA2.StoreContext.GetCollection<SampleTaskModel>().First().Subject).IsEqualTo("A's private task");

		// Negative: the other-stream peer must never see it.
		await Task.Delay(LeakGraceMs);
		await Assert.That(nodeB.StoreContext.GetCollection<SampleTaskModel>()).HasCount(0);
	}

	[Test]
	public async Task Backlog_replay_is_not_delivered_across_streams()
	{
		var streamA = Guid.NewGuid();
		var streamB = Guid.NewGuid();

		var master = new SynqraTestNode(Guid.NewGuid(), builder => { }, masterHost: true, useRealEndpoint: true);
		await master.Started;

		// Write stream A's history to the master BEFORE any of the connecting peers exist — this is the
		// backlog path (replayed from the master's own log on connect), distinct from live broadcast.
		var nodeA = new SynqraTestNode(streamA, builder => { }) { Port = master.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "A's pre-existing task" });

		var sw = Stopwatch.StartNew();
		while (!await MasterHasAnyEventAsync(master) && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		await Assert.That(await MasterHasAnyEventAsync(master)).IsTrue();

		// A brand-new stream-A client (cold cursor) must receive the backlog...
		var nodeC = new SynqraTestNode(streamA, builder => { }) { Port = master.Port };
		// ...and a brand-new stream-B client must NOT, though it reads the same shared master log.
		var nodeD = new SynqraTestNode(streamB, builder => { }) { Port = master.Port };
		await nodeC.Started;
		await nodeD.Started;
		await WaitForOnlineAsync(nodeC);
		await WaitForOnlineAsync(nodeD);

		var cGot = await WaitForCountAsync(nodeC, 1, PropagationTimeoutMs);
		await Assert.That(cGot).IsTrue();
		await Assert.That(nodeC.StoreContext.GetCollection<SampleTaskModel>().First().Subject).IsEqualTo("A's pre-existing task");

		await Task.Delay(LeakGraceMs);
		await Assert.That(nodeD.StoreContext.GetCollection<SampleTaskModel>()).HasCount(0);
	}

	static async Task<bool> MasterHasAnyEventAsync(SynqraTestNode master)
	{
		await foreach (var _ in master.Events.GetAllAsync())
		{
			return true;
		}
		return false;
	}

	static async Task<bool> WaitForCountAsync(SynqraTestNode node, int expected, int timeoutMs)
	{
		var sw = Stopwatch.StartNew();
		while (node.StoreContext.GetCollection<SampleTaskModel>().Count < expected)
		{
			if (sw.ElapsedMilliseconds > timeoutMs && !Debugger.IsAttached)
			{
				return false;
			}
			await Task.Delay(100);
		}
		return true;
	}

	static async Task WaitForOnlineAsync(SynqraTestNode node)
	{
		var sw = Stopwatch.StartNew();
		while (!node.StoreContext.IsOnline())
		{
			if (sw.ElapsedMilliseconds > 30_000 && !Debugger.IsAttached)
			{
				throw new TimeoutException("Node did not come online within 30s");
			}
			await Task.Delay(100);
		}
	}
}
#endif
