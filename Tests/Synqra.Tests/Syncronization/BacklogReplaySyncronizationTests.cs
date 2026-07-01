using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// A connecting client used to get nothing but future events (see
/// SynqraReplicationEndpointExtensions.cs's own remarks) — a brand-new device, a cleared
/// browser, or a second browser for the same account saw none of a stream's existing history.
/// These pin the fix: the master now replays a stream's full backlog (via the client's HELLO
/// cursor) to any (re)connecting client, keyed by what that specific client has already seen.
/// </summary>
[NotInParallel]
internal class BacklogReplaySyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 10_000;

	[Test]
	public async Task Should_replay_existing_history_to_a_client_that_connects_after_the_data_already_exists()
	{
		var nodeMaster = new SynqraTestNode(builder => { }, masterHost: true);
		await nodeMaster.Started;

		// Node A writes data and syncs it to the master BEFORE node C ever exists — the
		// scenario the old protocol silently failed: a client's history predates the
		// connecting client by definition, since it never even existed yet.
		var nodeA = new SynqraTestNode(builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);

		var collectionA = nodeA.StoreContext.GetCollection<SampleTaskModel>();
		var task = new SampleTaskModel { Subject = "Pre-existing task" };
		collectionA.Add(task);

		// Confirm it actually reached the master's own durable store before node C connects —
		// otherwise this test would pass by accident (a live-broadcast race, not a real
		// backlog replay).
		var sw = Stopwatch.StartNew();
		while (!await MasterHasEventForAsync(nodeMaster) && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		await Assert.That(await MasterHasEventForAsync(nodeMaster)).IsTrue();

		// Node C has never connected before — a cold LastEventIdFromServer cursor, exactly
		// like a brand-new device or a cleared browser.
		var nodeC = new SynqraTestNode(builder => { }) { Port = nodeMaster.Port };
		await nodeC.Started;
		await WaitForOnlineAsync(nodeC);

		var collectionC = nodeC.StoreContext.GetCollection<SampleTaskModel>();
		sw = Stopwatch.StartNew();
		while (collectionC.Count < 1 && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		await Assert.That(collectionC).HasCount(1);
		await Assert.That(collectionC.First().Subject).IsEqualTo("Pre-existing task");
	}

	static async Task<bool> MasterHasEventForAsync(SynqraTestNode master)
	{
		await foreach (var _ in master.Events.GetAllAsync())
		{
			return true;
		}
		return false;
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
