using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// A client hears the master's answer to each own event: stored, or refused with its reason. A refused event's local effect
/// is undone in the client's own log and never sent; a failure to store is answered as a refusal, never left silent.
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
	public async Task A_failure_to_store_is_answered_as_a_refusal()
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

		await WaitUntilAsync(() => own.All(ev => verdicts.ContainsKey(ev.EventId)), "the failure is answered, not left silent");
		var refusal = verdicts[own.First(ev => ev is ComponentAddedEvent).EventId].Refusal;
		await Assert.That(refusal).Contains("could not store");
		await Assert.That(refusal).DoesNotContain("the store is down").Because("the failure's own words stay on the server");
		await WaitUntilAsync(() => !nodeA.StoreContext.GetCollection<SampleTaskModel>().Any(), "the write that was not stored is gone from the client");
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
