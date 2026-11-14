using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// A client hears the master's answer to each own event: stored, or refused with its reason. A refused event's local effect
/// is undone in the client's own log and never sent; a failure to apply is answered as a refusal, never left silent (a failure to store is resent, see Synqra#205).
/// </summary>
internal class WriteVerdictSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;

	[Test]
	public async Task A_stored_event_gets_a_verdict_naming_its_command()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		var verdicts = Listen(nodeA);
		await WaitForOnlineAsync(nodeA);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "stored" });
		var own = await EventsAsync(nodeA.Events);

		await WaitUntilAsync(() => own.All(ev => verdicts.ContainsKey(ev.EventId)), "every own event gets a verdict");
		foreach (var ev in own)
		{
			await Assert.That(verdicts[ev.EventId].Refusal).IsNull();
			await Assert.That(verdicts[ev.EventId].CommandId).IsEqualTo(ev.CommandId);
		}
	}

	[Test]
	public async Task A_refused_write_is_undone_locally_and_its_verdict_says_why()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IProjection>(new RefusingProjection()), masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		var verdicts = Listen(nodeA);
		await WaitForOnlineAsync(nodeA);

		var tasks = nodeA.StoreContext.GetCollection<SampleTaskModel>();
		tasks.Add(new SampleTaskModel { Subject = "refused" });
		await Assert.That(tasks.Count()).IsEqualTo(1).Because("the write shows locally at once");
		var own = await EventsAsync(nodeA.Events);

		await WaitUntilAsync(() => own.All(ev => verdicts.ContainsKey(ev.EventId)), "every own event gets a verdict");
		await Assert.That(own.Where(ev => ev is ComponentAddedEvent).All(ev => verdicts[ev.EventId].Refusal is { Length: > 0 })).IsTrue();
		await WaitUntilAsync(() => !nodeA.StoreContext.GetCollection<SampleTaskModel>().Any(), "the refused add is gone from the client");
		var log = await EventsAsync(nodeA.Events);
		await Assert.That(log.Any(ev => ev.IsLocalUndo)).IsTrue().Because("the undo is in the client's own log, so a reload folds it too");
		await Task.Delay(1000); // a send loop that sends the undo would have done so by now
		await Assert.That(Replication(nodeA).FramesSent).IsEqualTo(own.Count).Because("an undo is never sent");
	}

	[Test]
	public async Task A_failure_to_apply_is_answered_as_a_refusal()
	{
		var streamId = Guid.NewGuid();
		var failing = new RefusingProjection(ev => new IOException("the store is down"));
		var nodeMaster = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IProjection>(failing), masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		var verdicts = Listen(nodeA);
		await WaitForOnlineAsync(nodeA);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "not stored" });
		var own = await EventsAsync(nodeA.Events);

		await WaitUntilAsync(() => own.All(ev => verdicts.ContainsKey(ev.EventId)), "the apply failure is answered, not left silent");
		var refusal = verdicts[own.First(ev => ev is ComponentAddedEvent).EventId].Refusal;
		await Assert.That(refusal).Contains("could not apply");
		await Assert.That(refusal).DoesNotContain("the store is down").Because("the failure's own words stay on the server");
		await WaitUntilAsync(() => !nodeA.StoreContext.GetCollection<SampleTaskModel>().Any(), "the write the master could not apply is gone from the client");
	}

	/// <summary>
	/// The refused events come from an earlier run's log, so they are read back from the file: a stored event has no stream, and
	/// the undo takes the connection's (CI 15598/15603: an undo with no stream was refused by the log, every connect, forever).
	/// </summary>
	[Test]
	public async Task A_refused_write_of_an_earlier_run_is_undone_too()
	{
		var streamId = Guid.NewGuid();
		var dyingMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, acknowledgeInbound: false);
		await dyingMaster.Started;
		var remembered = new RememberedState();
		var firstRun = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IEventReplicationStateStore>(remembered)) { Port = dyingMaster.Port };
		await firstRun.Started;
		await WaitForOnlineAsync(firstRun);
		firstRun.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "written in an earlier run" });
		await Task.Delay(1000); // sent, never answered
		var stranded = await EventsAsync(firstRun.Events);
		var logFile = firstRun.Host.Services.GetRequiredService<IConfiguration>()[LogFileKey] ?? throw new InvalidOperationException("The node has no JSON-lines log file.");
		await firstRun.Host.StopAsync();
		await ((IAsyncDisposable)firstRun.Host).DisposeAsync();

		var refusingMaster = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IProjection>(new RefusingProjection()), masterHost: true, useRealEndpoint: true);
		await refusingMaster.Started;
		var nextRun = new SynqraTestNode(streamId, builder =>
		{
			builder.Configuration[LogFileKey] = logFile;
			builder.Services.AddSingleton<IEventReplicationStateStore>(new RememberedState(remembered.Saved));
		}) { Port = refusingMaster.Port };
		await nextRun.Started;
		var verdicts = Listen(nextRun);
		await WaitForOnlineAsync(nextRun);

		await WaitUntilAsync(() => stranded.All(ev => verdicts.ContainsKey(ev.EventId)), "every stranded event gets a verdict");
		await Assert.That(stranded.All(ev => verdicts[ev.EventId].Refusal is { Length: > 0 })).IsTrue();
		await WaitUntilAsync(() => !nextRun.StoreContext.GetCollection<SampleTaskModel>().Any(), "the refused add is gone from the next run's store");
		await Assert.That((await EventsAsync(nextRun.Events)).Any(ev => ev.IsLocalUndo)).IsTrue();
	}

	/// <summary>
	/// The user changes the same field again while the undo of their refused change reads the log. That later write is stored by the
	/// master, so the client must end on it: an undo computed from the read alone and appended after the write put the old value
	/// back over it, and the client and the master stayed apart until a reload (review of Synqra#206).
	/// </summary>
	[Test]
	public async Task A_write_made_while_the_undo_reads_the_log_is_not_undone()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IInboundEventGuard>(new ValueGuard("refused")), masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var log = new InterruptedLog();
		var nodeA = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IAppendStorage<Event, (Guid, Guid)>>(log)) { Port = nodeMaster.Port };
		await nodeA.Started;
		var verdicts = Listen(nodeA);
		await WaitForOnlineAsync(nodeA);
		var task = new SampleTaskModel { Subject = "first" };
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(task);
		var added = await EventsAsync(nodeA.Events);
		await WaitUntilAsync(() => added.All(ev => verdicts.ContainsKey(ev.EventId)), "the add is stored");

		log.Interrupt(() => task.Subject = "later"); // the next whole-log read, the undo's, lets the user write in its middle
		task.Subject = "refused";

		await WaitUntilAsync(() => verdicts.Values.Any(verdict => verdict.Refusal is { Length: > 0 }), "the change is refused");
		await WaitUntilAsync(() => log.Interrupted, "the user wrote while the undo read the log");
		var all = await EventsAsync(nodeA.Events);
		await WaitUntilAsync(() => all.Where(ev => !ev.IsLocalUndo).All(ev => verdicts.ContainsKey(ev.EventId)), "the later write is answered too");
		await Task.Delay(500); // the keeper folds what the log holds
		await Assert.That(nodeA.StoreContext.GetCollection<SampleTaskModel>().Single().Subject).IsEqualTo("later").Because("the master stored the later write; the undo must not take it back");
	}

	/// <summary>Refuses a change of any property to one value.</summary>
	sealed class ValueGuard(string refused) : IInboundEventGuard
	{
		public ValueTask<string?> RefuseAsync(Event ev, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(ev is ComponentPropertyChangedEvent { NewValue: var value } && Equals(value?.ToString(), refused) ? $"'{refused}' is refused" : null);
	}

	/// <summary>An in-memory local log whose next whole read runs an action after taking what it returns: a write made during that read.</summary>
	sealed class InterruptedLog : IAppendStorage<Event, (Guid, Guid)>
	{
		readonly SortedDictionary<(Guid, Guid), Event> _events = new(Comparer<(Guid, Guid)>.Create((a, b) => a.Item1.CompareBigEndian(b.Item1) is var first && first != 0 ? first : a.Item2.CompareBigEndian(b.Item2)));
		Action? _interruption;

		public bool Interrupted { get; private set; }

		public void Interrupt(Action interruption) => _interruption = interruption;

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
				after = [.. _events.Where(e => from == default || _events.Comparer.Compare(e.Key, from) > 0).Select(e => e.Value)];
			}
			var interruption = from == default ? Interlocked.Exchange(ref _interruption, null) : null;
			if (interruption is not null)
			{
				interruption();
				Interrupted = true;
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

	const string LogFileKey = "Storage:JsonLinesStorage:FileName";

	sealed class RememberedState(EventReplicationState? remembered = null) : IEventReplicationStateStore
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

	ConcurrentDictionary<Guid, EventVerdict> Listen(SynqraTestNode node)
	{
		var verdicts = new ConcurrentDictionary<Guid, EventVerdict>();
		Replication(node).EventSettled += verdict => verdicts[verdict.EventId] = verdict;
		return verdicts;
	}

	EventReplicationService Replication(SynqraTestNode node) => node.Host.Services.GetRequiredService<EventReplicationService>();

	async Task<List<Event>> EventsAsync(IEventLog log)
	{
		var events = new List<Event>();
		await foreach (var ev in log.GetAllAsync())
		{
			events.Add(ev);
		}
		return events;
	}

	async Task WaitUntilAsync(Func<bool> condition, string what)
	{
		var sw = Stopwatch.StartNew();
		while (!condition() && (sw.ElapsedMilliseconds < PropagationTimeoutMs || Debugger.IsAttached))
		{
			await Task.Delay(100);
		}
		await Assert.That(condition()).IsTrue().Because($"timed out waiting until {what}");
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
