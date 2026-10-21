using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System.Diagnostics;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// A client's outbound cursor (LastEventIdFromMe) moves only when the master acknowledges an event, and whatever is
/// still unacknowledged is offered on connect without waiting for a new local write. Prod 2026-10-20: a browser wrote
/// 104 events into a socket whose server side had already died, marked them sent, and never offered them again.
/// </summary>
[NotInParallel]
internal class OutboundCursorSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;
	const string LogFileKey = "Storage:JsonLinesStorage:FileName";

	[Test]
	public async Task The_cursor_moves_on_the_masters_acknowledgement()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await nodeMaster.Started;
		var remembered = new RecordingStateStore();
		var nodeA = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IEventReplicationStateStore>(remembered)) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "acknowledged" });

		await WaitUntilAsync(async () => await CountEventsAsync(nodeMaster) > 0, "the write reaches the master");
		await WaitUntilAsync(() => Task.FromResult(remembered.Saved?.LastEventIdFromMe != default), "the cursor moves once the master acknowledges");
	}

	[Test]
	public async Task The_cursor_stays_when_the_master_does_not_acknowledge()
	{
		var streamId = Guid.NewGuid();
		var dyingMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, acknowledgeInbound: false);
		await dyingMaster.Started;
		var remembered = new RecordingStateStore();
		var nodeA = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IEventReplicationStateStore>(remembered)) { Port = dyingMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "stranded" });
		await Task.Delay(2000); // the frame is written and nothing comes back

		await Assert.That(await CountEventsAsync(dyingMaster)).IsEqualTo(0);
		await Assert.That(remembered.Saved?.LastEventIdFromMe ?? default).IsEqualTo(default(Guid)).Because("an event the master never acknowledged is not sent");
	}

	[Test]
	public async Task Unacknowledged_events_are_offered_on_connect_without_a_new_write()
	{
		var streamId = Guid.NewGuid();
		// A's first run writes into a dying master: its log holds the events, its cursor says none were acknowledged.
		var dyingMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, acknowledgeInbound: false);
		await dyingMaster.Started;
		var remembered = new RecordingStateStore();
		var nodeA = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IEventReplicationStateStore>(remembered)) { Port = dyingMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "written while the master was dying" });
		await Task.Delay(1000);
		var logA = await EndRunAsync(nodeA);
		await Assert.That(await CountEventsAsync(dyingMaster)).IsEqualTo(0);

		// A's next run connects to a healthy master and writes nothing: the stranded events arrive on connect alone.
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeB = new SynqraTestNode(streamId, builder =>
		{
			builder.Configuration[LogFileKey] = logA;
			builder.Services.AddSingleton<IEventReplicationStateStore>(new RecordingStateStore(remembered.Saved));
		}) { Port = nodeMaster.Port };
		await nodeB.Started;
		await WaitForOnlineAsync(nodeB);

		// Every event of A's first run is now in the master's log: nothing B wrote, since B wrote nothing.
		var stranded = await EventIdsAsync(nodeB.Events);
		await Assert.That(stranded.Count).IsGreaterThan(0);
		await WaitUntilAsync(async () => (await EventIdsAsync(nodeMaster.Events)).IsSupersetOf(stranded), "the stranded events reach the healthy master with no new write");
	}

	async Task<HashSet<Guid>> EventIdsAsync(IEventLog log)
	{
		var ids = new HashSet<Guid>();
		await foreach (var ev in log.GetAllAsync())
		{
			ids.Add(ev.EventId);
		}
		return ids;
	}

	async Task<string> EndRunAsync(SynqraTestNode node)
	{
		var logFile = node.Host.Services.GetRequiredService<IConfiguration>()[LogFileKey] ?? throw new InvalidOperationException("The node has no JSON-lines log file.");
		await node.Host.StopAsync();
		await ((IAsyncDisposable)node.Host).DisposeAsync();
		return logFile;
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

	sealed class RecordingStateStore(EventReplicationState? remembered = null) : IEventReplicationStateStore
	{
		public EventReplicationState? Saved { get; private set; } = remembered;

		public Task<EventReplicationState?> LoadAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(Saved is null ? null : Copy(Saved));

		public Task SaveAsync(Guid streamId, EventReplicationState state, CancellationToken cancellationToken = default)
		{
			Saved = Copy(state);
			return Task.CompletedTask;
		}

		EventReplicationState Copy(EventReplicationState state) => new() { MyNodeId = state.MyNodeId, LastEventIdFromMe = state.LastEventIdFromMe, ServerWatermark = state.ServerWatermark };
	}
}
