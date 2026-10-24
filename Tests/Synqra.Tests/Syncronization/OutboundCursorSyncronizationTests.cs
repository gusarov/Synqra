using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;
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

	/// <summary>
	/// An event the master's log already holds is acknowledged, not applied again: after a restart the master's in-memory
	/// set is empty, and prod 2026-10-22 applied such re-offers to the projection, which refused them (a facet in a slot
	/// already filled), so the client's cursor never moved and it offered them on every connect, forever.
	/// </summary>
	[Test]
	public async Task The_cursor_moves_for_an_event_the_masters_log_already_holds()
	{
		var streamId = Guid.NewGuid();
		var firstMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await firstMaster.Started;
		var remembered = new RecordingStateStore();
		var nodeA = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IEventReplicationStateStore>(remembered)) { Port = firstMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "held by the master" });
		var written = await EventIdsAsync(nodeA.Events);
		await WaitUntilAsync(async () => (await EventIdsAsync(firstMaster.Events)).IsSupersetOf(written), "the first master holds every event A wrote");
		var logA = await EndRunAsync(nodeA);
		var held = new List<Event>();
		await foreach (var ev in firstMaster.Events.GetAllAsync())
		{
			held.Add(ev);
		}

		// The master restarts: the same stored log, nothing in memory (its storage is filled below the log, so no commit is
		// reported and nothing is marked known), and a projection that refuses every apply since the state these events
		// produced is there already.
		var refusing = new RefusingProjection();
		var restartedMaster = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IProjection>(refusing), masterHost: true, useRealEndpoint: true);
		await restartedMaster.Started;
		var storedLog = restartedMaster.Host.Services.GetRequiredService<IAppendStorage<Event, (Guid, Guid)>>();
		var tail = default(Guid);
		foreach (var ev in held)
		{
			await storedLog.AppendAsync(ev);
			tail = ev.SequenceId.CompareBigEndian(tail) > 0 ? ev.SequenceId : tail;
		}

		// A's next run lost the acknowledgement (a cold outbound cursor) but holds the tail: no backlog comes down, and every event goes up again.
		var offeredAgain = new RecordingStateStore(new EventReplicationState { MyNodeId = remembered.Saved!.MyNodeId, ServerWatermark = tail, LastEventIdFromMe = default });
		var nodeB = new SynqraTestNode(streamId, builder =>
		{
			builder.Configuration[LogFileKey] = logA;
			builder.Services.AddSingleton<IEventReplicationStateStore>(offeredAgain);
		}) { Port = restartedMaster.Port };
		await nodeB.Started;
		await WaitForOnlineAsync(nodeB);
		var lastOffered = default(Guid);
		await foreach (var ev in nodeB.Events.GetAllAsync())
		{
			lastOffered = ev.EventId;
		}

		await WaitUntilAsync(() => Task.FromResult(offeredAgain.Saved?.LastEventIdFromMe == lastOffered), "the restarted master acknowledges what its log already holds");
		await Assert.That(refusing.Applied).IsEqualTo(0).Because("an event the log holds is never applied again");
	}

	/// <summary>
	/// An event the master's projection refuses will never be in its log: the master says so (EventRejected) and the
	/// client's cursor moves past it, so it is not offered again on every connect (prod 2026-10-22: ~100 refused applies
	/// per connect under the broadcast gate).
	/// </summary>
	[Test]
	public async Task The_cursor_moves_past_an_event_the_master_refuses()
	{
		var streamId = Guid.NewGuid();
		var refusing = new RefusingProjection();
		var nodeMaster = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IProjection>(refusing), masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var remembered = new RecordingStateStore();
		var nodeA = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IEventReplicationStateStore>(remembered)) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "refused by the master" });
		var lastWritten = default(Guid);
		await foreach (var ev in nodeA.Events.GetAllAsync())
		{
			lastWritten = ev.EventId;
		}

		await WaitUntilAsync(() => Task.FromResult(remembered.Saved?.LastEventIdFromMe == lastWritten), "the cursor moves past what the master refused");
		await Assert.That(refusing.Applied).IsGreaterThan(0).Because("the master tried and refused");
		await Assert.That(await CountEventsAsync(nodeMaster)).IsEqualTo(0).Because("a refused event is not in the master's log");
	}

	[Test]
	public async Task A_client_does_not_echo_what_the_master_sent_it()
	{
		// Prod 2026-10-22: after a Force Resync the browser's cursor was empty, so it sent its whole received history back,
		// one frame and one ack per event, and the page stopped responding while it did.
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);
		for (var i = 0; i < 5; i++)
		{
			nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = $"history {i}" });
		}
		var history = await EventIdsAsync(nodeA.Events);
		await WaitUntilAsync(async () => (await EventIdsAsync(nodeMaster.Events)).IsSupersetOf(history), "A's history reaches the master");

		var nodeB = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeB.Started;
		await WaitForOnlineAsync(nodeB);
		await WaitUntilAsync(async () => (await EventIdsAsync(nodeB.Events)).IsSupersetOf(history), "the history reaches B");
		await Task.Delay(1000); // a send loop that echoes would have done so by now

		await Assert.That(Replication(nodeB).FramesSent).IsEqualTo(0).Because("B received the history; it must not send it back");
		nodeB.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "B's own write" });
		var own = (await EventIdsAsync(nodeB.Events)).Except(history).ToHashSet();
		await WaitUntilAsync(async () => (await EventIdsAsync(nodeMaster.Events)).IsSupersetOf(own), "B's own write reaches the master");
		await Assert.That(Replication(nodeB).FramesSent).IsEqualTo(own.Count).Because("only B's own events are sent");
	}

	[Test]
	public async Task A_cursor_missing_from_the_log_does_not_stop_sending()
	{
		// The handoff of 2026-10-22: a kept state naming an event the local log no longer holds made the send loop skip
		// forever, so the owner's browser connected and sent nothing.
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "first run" });
		await WaitUntilAsync(async () => (await EventIdsAsync(nodeMaster.Events)).IsSupersetOf(await EventIdsAsync(nodeA.Events)), "A's first run reaches the master");
		var logA = await EndRunAsync(nodeA);

		var lost = new EventReplicationState { MyNodeId = Guid.NewGuid(), LastEventIdFromMe = Guid.NewGuid() }; // not in A's log
		var nodeB = new SynqraTestNode(streamId, builder =>
		{
			builder.Configuration[LogFileKey] = logA;
			builder.Services.AddSingleton<IEventReplicationStateStore>(new RecordingStateStore(lost));
		}) { Port = nodeMaster.Port };
		await nodeB.Started;
		await WaitForOnlineAsync(nodeB);
		var before = await EventIdsAsync(nodeB.Events);

		nodeB.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "written under a lost cursor" });
		var own = (await EventIdsAsync(nodeB.Events)).Except(before).ToHashSet();
		await Assert.That(own.Count).IsGreaterThan(0);
		await WaitUntilAsync(async () => (await EventIdsAsync(nodeMaster.Events)).IsSupersetOf(own), "the write reaches the master despite the lost cursor");
	}

	EventReplicationService Replication(SynqraTestNode node) => node.Host.Services.GetRequiredService<EventReplicationService>();

	/// <summary>
	/// A local write wakes the send loop, which used to read the whole local log again to find what follows the last sent
	/// event: seconds per click on a large stream (WI 487). After its first pass a run reads only what follows.
	/// </summary>
	[Test]
	public async Task A_local_write_reads_only_what_follows_the_last_sent_event()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await nodeMaster.Started;
		var log = new CountingLog();
		var nodeA = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IAppendStorage<Event, (Guid, Guid)>>(log)) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "first" });
		await WaitUntilAsync(async () => await CountEventsAsync(nodeMaster) > 0, "the first write reaches the master");
		var afterFirst = await CountEventsAsync(nodeMaster);
		var fullReads = log.FullReads;

		for (var i = 0; i < 3; i++)
		{
			nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = $"more {i}" });
		}
		await WaitUntilAsync(async () => await CountEventsAsync(nodeMaster) >= afterFirst * 4, "the later writes reach the master");

		await Assert.That(log.FullReads).IsEqualTo(fullReads).Because("later writes read only what follows the last sent event");
	}

	/// <summary>An in-memory local log, keyed like a client's, that counts reads from the start.</summary>
	sealed class CountingLog : IAppendStorage<Event, (Guid, Guid)>
	{
		readonly SortedDictionary<(Guid, Guid), Event> _events = new(Comparer<(Guid, Guid)>.Create((a, b) => a.Item1.CompareBigEndian(b.Item1) is var first && first != 0 ? first : a.Item2.CompareBigEndian(b.Item2)));

		public int FullReads { get; private set; }

		public Task AppendAsync(Event item, CancellationToken cancellationToken = default)
		{
			lock (_events)
			{
				_events.Add((item.StreamId, item.SequenceId), item);
			}
			return Task.CompletedTask;
		}

		public Task<Event> GetAsync((Guid, Guid) key, CancellationToken cancellationToken = default)
		{
			lock (_events)
			{
				return Task.FromResult(_events[key]);
			}
		}

		public async IAsyncEnumerable<Event> GetAllAsync((Guid, Guid) from = default, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			List<Event> after;
			lock (_events)
			{
				if (from == default)
				{
					FullReads++;
				}
				after = [.. _events.Where(e => from == default || _events.Comparer.Compare(e.Key, from) > 0).Select(e => e.Value)];
			}
			foreach (var ev in after)
			{
				await Task.Yield();
				yield return ev;
			}
		}

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
