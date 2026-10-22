using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synqra.AppendStorage;
using Synqra.AppendStorage.InMemory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions.Extensions;

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
	// Matches the 30s online-wait budget (see WaitForOnlineAsync): under docker CPU contention a
	// node can spend most of that budget just connecting, so a tighter propagation window
	// occasionally lapses under load even though no event is dropped. Polling exits on first
	// success, so the happy path never waits the full budget.
	const int PropagationTimeoutMs = 30_000;

	[Test]
	public async Task Should_replay_existing_history_to_a_client_that_connects_after_the_data_already_exists()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await nodeMaster.Started;

		// Node A writes data and syncs it to the master BEFORE node C ever exists — the
		// scenario the old protocol silently failed: a client's history predates the
		// connecting client by definition, since it never even existed yet.
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
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

		// Node C has never connected before — a cold ServerWatermark cursor, exactly
		// like a brand-new device or a cleared browser.
		var nodeC = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
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

#if NET10_0_OR_GREATER
	// Only the real endpoint (net10) sends watermarks; on older targets the test node falls back to a master that does not.
	/// <summary>
	/// A client that remembers the server's watermark (docs/model.md §9) resumes with the delta: on its next run, with its
	/// own log and state, the history it holds is not sent again, only what the stream committed after it.
	/// </summary>
	[Test]
	public async Task Should_resume_from_the_remembered_watermark_with_the_delta_only()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "first" });
		await WaitUntilAsync(async () => await CountEventsAsync(nodeMaster) > 0, "the first write reaches the master");

		// C syncs the whole stream, remembers where it got to, and its run ends.
		var remembered = new RecordingStateStore();
		var nodeC = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IEventReplicationStateStore>(remembered)) { Port = nodeMaster.Port };
		await nodeC.Started;
		await WaitForOnlineAsync(nodeC);
		await WaitUntilAsync(() => Task.FromResult(nodeC.StoreContext.GetCollection<SampleTaskModel>().Count == 1), "C holds the first task");
		await WaitUntilAsync(() => Task.FromResult(remembered.Saved?.ServerWatermark != default), "C persisted a watermark");
		var logC = await EndRunAsync(nodeC);
		var afterC = await CountEventsAsync(nodeMaster);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "second" });
		await WaitUntilAsync(async () => await CountEventsAsync(nodeMaster) > afterC, "the second write reaches the master");
		var delta = await CountWhenSettledAsync(nodeMaster) - afterC; // the write's events land one by one: counted once all have

		// D is C's next run: C's log and C's state. It holds both tasks and was sent only the second write's events.
		var nodeD = NextRun(nodeMaster, streamId, logC, remembered.Saved);
		await nodeD.Started;
		await WaitForOnlineAsync(nodeD);
		var collectionD = nodeD.StoreContext.GetCollection<SampleTaskModel>();
		await WaitUntilAsync(() => Task.FromResult(collectionD.Count == 2), "D holds both tasks");
		await Task.Delay(500); // nothing more may follow

		await Assert.That(collectionD.Select(t => t.Subject)).IsEquivalentTo(["first", "second"]);
		await Assert.That(FramesReceived(nodeD)).IsEqualTo(delta);
	}

	/// <summary>
	/// The watermark names an event the client already holds, so the resume starts after it: with the watermark on the
	/// first task's own event, the frames sent are exactly the events after it.
	/// </summary>
	[Test]
	public async Task Should_resume_after_the_watermark_not_at_it()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "first" });
		await WaitUntilAsync(async () => await FirstSequenceOfAsync<ComponentAddedEvent>(nodeMaster) != default, "the first task's event reaches the master");
		var atFirstTask = await FirstSequenceOfAsync<ComponentAddedEvent>(nodeMaster);

		var nodeC = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeC.Started;
		await WaitForOnlineAsync(nodeC);
		await WaitUntilAsync(() => Task.FromResult(nodeC.StoreContext.GetCollection<SampleTaskModel>().Count == 1), "C holds the first task");
		var logC = await EndRunAsync(nodeC);

		var afterC = await CountEventsAsync(nodeMaster);
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "second" });
		await WaitUntilAsync(async () => await CountEventsAsync(nodeMaster) > afterC, "the second write reaches the master");
		await CountWhenSettledAsync(nodeMaster); // the write's events land one by one: counted once all have
		var afterWatermark = 0;
		await foreach (var ev in nodeMaster.Events.ReadStreamAsync(streamId))
		{
			if (ev.SequenceId.CompareBigEndian(atFirstTask) > 0)
			{
				afterWatermark++;
			}
		}

		var nodeD = NextRun(nodeMaster, streamId, logC, new EventReplicationState { ServerWatermark = atFirstTask });
		await nodeD.Started;
		await WaitForOnlineAsync(nodeD);
		await WaitUntilAsync(() => Task.FromResult(nodeD.StoreContext.GetCollection<SampleTaskModel>().Count == 2), "D holds both tasks");
		await Task.Delay(500); // nothing more may follow

		await Assert.That(FramesReceived(nodeD)).IsEqualTo(afterWatermark);
	}

	/// <summary>
	/// A client whose run ends in the middle of a backlog replay resumes where it stopped, never past events it has not
	/// received. Prod 2026-10-22: every backlog frame carried the watermark taken at connect, the browser persisted it on
	/// the first frame, a reload mid-replay then asked for the delta after the tail, and the events in between (47 nodes
	/// written through the MCP connector) never came again.
	/// </summary>
	[Test]
	public async Task Should_resume_an_interrupted_replay_without_a_hole()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);
		for (var i = 0; i < 20; i++)
		{
			nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = $"task {i}" });
		}
		await WaitUntilAsync(async () => await CountEventsAsync(nodeMaster) >= 20, "the writes reach the master");
		var total = await CountWhenSettledAsync(nodeMaster);

		// C's run ends while the state of its first frame is being saved: it holds that frame's event and the cursor it persisted.
		// One frame per batch, so the first save is the first frame's and the run ends with the rest of the backlog unreceived.
		var interrupted = new InterruptingStateStore();
		var nodeC = new SynqraTestNode(streamId, builder =>
		{
			builder.Services.AddSingleton<IEventReplicationStateStore>(interrupted);
			builder.Services.AddSingleton(new EventReplicationConfig
			{
				Endpoint = $"ws://localhost:{nodeMaster.Port}/api/synqra/ws?stream={streamId:D}",
				MaxReceiveBatch = 1,
			});
		}) { Port = nodeMaster.Port };
		await nodeC.Started;
		await interrupted.FirstWatermarkSave.Task.WaitAsync(TimeSpan.FromMilliseconds(PropagationTimeoutMs));
		var heldByC = await CountEventsAsync(nodeC);
		var logC = await EndRunAsync(nodeC);
		interrupted.Release(); // the reader sees the cancelled run and stops; nothing more lands in C's log
		await Assert.That(heldByC).IsLessThan(total).Because("the run ended mid-replay");

		// D is C's next run: C's log and C's state. It must end up with the whole stream, sent the part C never got.
		var nodeD = NextRun(nodeMaster, streamId, logC, interrupted.Saved);
		await nodeD.Started;
		await WaitForOnlineAsync(nodeD);
		await WaitUntilAsync(async () => await CountEventsAsync(nodeD) == total, "D holds every event of the stream");
		await Task.Delay(500); // nothing more may follow

		await Assert.That(FramesReceived(nodeD)).IsEqualTo(total - heldByC);
	}

	/// <summary>
	/// A fresh client replaying a long backlog takes the frames that piled up while it stored the previous ones as one batch:
	/// one append, one state save and one <see cref="IEventReplicationService.EventsReceived"/> per batch. Each save here
	/// costs 50 ms, a stand-in for a slow browser's storage: a save per event would need minutes, far over the bound. Quotaly's
	/// Case_65 on a one-core agent: a fresh browser took minutes to replay the demo user's stream before seeing a new write.
	/// </summary>
	[Test]
	public async Task Should_replay_a_long_backlog_to_a_fresh_client_in_batches()
	{
		const int Tasks = 1500;
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var tasks = nodeMaster.StoreContext.GetCollection<SampleTaskModel>();
		for (var i = 0; i < Tasks; i++)
		{
			tasks.Add(new SampleTaskModel { Subject = $"task {i}" });
		}
		var total = await CountEventsAsync(nodeMaster);
		var tail = default(Guid);
		await foreach (var ev in nodeMaster.Events.ReadStreamAsync(streamId))
		{
			tail = ev.SequenceId;
		}
		await Assert.That(total).IsGreaterThanOrEqualTo(Tasks);

		var slow = new SlowStateStore(TimeSpan.FromMilliseconds(50));
		var nodeC = new SynqraTestNode(streamId, builder =>
		{
			builder.Services.AddSingleton<IEventReplicationStateStore>(slow);
			// The JSON-lines log scans its whole file on every append; in memory, the time measured is the replication's.
			builder.Services.RemoveAll<IAppendStorage<Event, (Guid, Guid)>>();
			builder.Services.AddAppendStorageInMemory<Event, (Guid, Guid)>(x => (x.StreamId, x.SequenceId));
		}) { Port = nodeMaster.Port };
		var raised = 0;
		nodeC.Host.Services.GetRequiredService<IEventReplicationService>().EventsReceived += () => Interlocked.Increment(ref raised);
		await nodeC.Started;
		var sw = Stopwatch.StartNew();
		await WaitForOnlineAsync(nodeC);
		var collectionC = nodeC.StoreContext.GetCollection<SampleTaskModel>();
		await WaitUntilAsync(async () => collectionC.Count == Tasks && await CountEventsAsync(nodeC) == total, "C holds the whole backlog");
		var elapsed = sw.Elapsed;
		await Task.Delay(500); // nothing more may follow

		var ids = new HashSet<Guid>();
		await foreach (var ev in nodeC.Events.GetAllAsync())
		{
			ids.Add(ev.EventId);
		}
		await Assert.That(FramesReceived(nodeC)).IsEqualTo(total);
		await Assert.That(await CountEventsAsync(nodeC)).IsEqualTo(total).Because("a known id is never appended twice");
		await Assert.That(ids).HasCount(total);
		await Assert.That(slow.Saved?.ServerWatermark).IsEqualTo(tail).Because("the persisted watermark is the last frame's");
		await Assert.That(slow.Saves).IsLessThanOrEqualTo(total / 10).Because($"the state is saved once per batch, not per event ({slow.Saves} saves for {total} events)");
		await Assert.That(raised).IsLessThanOrEqualTo(total / 10).Because($"the owner is told once per batch, not per event ({raised} signals for {total} events)");
		await Assert.That(elapsed).IsLessThan(TimeSpan.FromMilliseconds(PropagationTimeoutMs)).Because($"a save per event alone would take {total * 50 / 1000}s");
	}

	/// <summary>Records like <see cref="RecordingStateStore"/>, counts the saves, and takes <paramref name="delay"/> for each: a slow client's storage.</summary>
	sealed class SlowStateStore(TimeSpan delay) : IEventReplicationStateStore
	{
		int _saves;

		public int Saves => Volatile.Read(ref _saves);

		public EventReplicationState? Saved { get; private set; }

		public Task<EventReplicationState?> LoadAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult<EventReplicationState?>(null);

		public async Task SaveAsync(Guid streamId, EventReplicationState state, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _saves);
			Saved = new() { MyNodeId = state.MyNodeId, LastEventIdFromMe = state.LastEventIdFromMe, ServerWatermark = state.ServerWatermark };
			await Task.Delay(delay, cancellationToken);
		}
	}

	/// <summary>The master's event count once two reads half a second apart agree: the writes in flight have landed.</summary>
	async Task<int> CountWhenSettledAsync(SynqraTestNode master)
	{
		var count = await CountEventsAsync(master);
		var settled = false;
		while (!settled)
		{
			await Task.Delay(500);
			var again = await CountEventsAsync(master);
			settled = again == count;
			count = again;
		}
		return count;
	}

	/// <summary>Records like <see cref="RecordingStateStore"/>, and holds the first save that carries a server watermark until released: a run is ended in that window.</summary>
	sealed class InterruptingStateStore : IEventReplicationStateStore
	{
		readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource FirstWatermarkSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public EventReplicationState? Saved { get; private set; }

		public Task<EventReplicationState?> LoadAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult<EventReplicationState?>(null);

		public async Task SaveAsync(Guid streamId, EventReplicationState state, CancellationToken cancellationToken = default)
		{
			Saved = new() { MyNodeId = state.MyNodeId, LastEventIdFromMe = state.LastEventIdFromMe, ServerWatermark = state.ServerWatermark };
			if (state.ServerWatermark != default && FirstWatermarkSave.TrySetResult())
			{
				await _release.Task;
			}
		}

		public void Release() => _release.TrySetResult();
	}

	const string LogFileKey = "Storage:JsonLinesStorage:FileName";

	/// <summary>Stops a client node and returns where its local log is, for its next run.</summary>
	async Task<string> EndRunAsync(SynqraTestNode node)
	{
		var logFile = node.Host.Services.GetRequiredService<IConfiguration>()[LogFileKey] ?? throw new InvalidOperationException("The node has no JSON-lines log file.");
		await node.Host.StopAsync();
		await ((IAsyncDisposable)node.Host).DisposeAsync(); // closes the log file, as a closed tab would
		return logFile;
	}

	/// <summary>A client node's next run: the previous run's log file, read afresh, and its replication state.</summary>
	SynqraTestNode NextRun(SynqraTestNode master, Guid streamId, string logFile, EventReplicationState? state)
		=> new(streamId, builder =>
		{
			builder.Configuration[LogFileKey] = logFile;
			builder.Services.AddSingleton<IEventReplicationStateStore>(new RecordingStateStore(state));
		}) { Port = master.Port };

	long FramesReceived(SynqraTestNode node) => node.Host.Services.GetRequiredService<EventReplicationService>().FramesReceived;

	async Task<Guid> FirstSequenceOfAsync<TEvent>(SynqraTestNode master)
		where TEvent : Event
	{
		var sequence = Guid.Empty;
		await foreach (var ev in master.Events.GetAllAsync())
		{
			if (sequence == Guid.Empty && ev is TEvent)
			{
				sequence = ev.SequenceId;
			}
		}
		return sequence;
	}
#endif

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

	/// <summary>The host's state store, in memory: what a client saved is what its next run loads.</summary>
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
