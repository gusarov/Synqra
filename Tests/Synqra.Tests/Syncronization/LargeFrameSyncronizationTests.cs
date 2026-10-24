#if NET10_0_OR_GREATER
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System.Diagnostics;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// An event larger than the replication frame's first buffer (8 KB) crosses the wire in both directions and in a
/// backlog replay. Prod 2026-10-19: a 450-command batch did not fit the frame, the replay died on it for every
/// reconnect, and the browser stayed two days behind without a word.
/// </summary>
internal class LargeFrameSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;

	// Several times the frame's first buffer, so the event cannot fit it by accident.
	static string LargeSubject(char fill) => new string(fill, 5 * EventReplicationService.DefaultFrameSize);

	[Test]
	public async Task A_large_event_is_uplinked_replayed_as_backlog_and_broadcast_live()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);

		// uplink: A's own frame outgrows the first buffer on the way to the master
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = LargeSubject('a') });
		await WaitUntilAsync(async () => await CountEventsAsync(nodeMaster) > 0, "the large write reaches the master");

		// backlog: C connects cold and is replayed the large event
		var nodeC = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeC.Started;
		await WaitForOnlineAsync(nodeC);
		var collectionC = nodeC.StoreContext.GetCollection<SampleTaskModel>();
		await WaitUntilAsync(() => Task.FromResult(collectionC.Count == 1), "C holds the large task from the backlog");
		await Assert.That(collectionC.First().Subject).IsEqualTo(LargeSubject('a'));

		// live: a second large event reaches C over the open connection
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = LargeSubject('b') });
		await WaitUntilAsync(() => Task.FromResult(collectionC.Count == 2), "C holds the second large task from the broadcast");
		await Assert.That(collectionC.Select(t => t.Subject)).IsEquivalentTo([LargeSubject('a'), LargeSubject('b')]);
	}

	async Task<int> CountEventsAsync(SynqraTestNode master)
	{
		var count = 0;
		await foreach (var _ in master.Events.GetAllAsync())
		{
			count++;
		}
		return count;
	}

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
}
#endif
