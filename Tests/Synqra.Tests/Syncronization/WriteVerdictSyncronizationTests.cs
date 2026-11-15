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
/// A client hears the master's answer to each own event: stored, or refused with its reason. A refusal changes nothing in the
/// client's log; a failure that may pass is no refusal: the connection ends and the client offers the event again.
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
		await Assert.That(own.OfType<ComponentAddedEvent>().Count()).IsEqualTo(1);

		await WaitUntilAsync(() => own.All(ev => verdicts.ContainsKey(ev.EventId)), "every own event gets a verdict");
		foreach (var ev in own)
		{
			await Assert.That(verdicts[ev.EventId].Refusal).IsNull();
			await Assert.That(verdicts[ev.EventId].CommandId).IsEqualTo(ev.CommandId);
		}
	}

	[Test]
	public async Task A_refused_write_gets_a_verdict_saying_why()
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
		await Assert.That(own.OfType<ComponentAddedEvent>().Count()).IsEqualTo(1);

		await WaitUntilAsync(() => own.All(ev => verdicts.ContainsKey(ev.EventId)), "every own event gets a verdict");
		await Assert.That(own.OfType<ComponentAddedEvent>().All(ev => verdicts[ev.EventId].Refusal is { Length: > 0 })).IsTrue();
		var log = await EventsAsync(nodeA.Events);
		await Assert.That(log.Select(ev => ev.EventId)).IsEquivalentTo(own.Select(ev => ev.EventId)).Because("a refusal adds nothing to the client's log: it holds what the client wrote");
		await Assert.That(tasks.Count()).IsEqualTo(1).Because("the client keeps what it wrote; the verdict tells the user");
		await Assert.That(Replication(nodeA).FramesSent).IsEqualTo(own.Count).Because("each own event is sent once, and nothing else");
	}

	/// <summary>
	/// The master's store is briefly unreachable while it applies the event: that is no refusal. The connection ends unanswered,
	/// the client offers the event again, and once the store answers the event is stored and its verdict says so.
	/// </summary>
	[Test]
	public async Task A_failure_to_apply_that_passes_is_resent_and_stored()
	{
		var streamId = Guid.NewGuid();
		var attempts = new ConcurrentDictionary<Guid, int>();
		var flaky = new RefusingProjection(ev => attempts.AddOrUpdate(ev.EventId, 1, (_, n) => n + 1) == 1 ? new IOException("the store is down") : null);
		var nodeMaster = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IProjection>(flaky), masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		var verdicts = Listen(nodeA);
		await WaitForOnlineAsync(nodeA);

		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "stored on the second try" });
		var own = await EventsAsync(nodeA.Events);
		await Assert.That(own.OfType<ComponentAddedEvent>().Count()).IsEqualTo(1);

		await WaitUntilAsync(() => own.All(ev => verdicts.ContainsKey(ev.EventId)), "every own event gets a verdict once the store answers");
		await Assert.That(own.All(ev => verdicts[ev.EventId].Refusal is null)).IsTrue().Because("a failure that passes is never a refusal");
		await Assert.That(own.All(ev => attempts[ev.EventId] >= 2)).IsTrue().Because("each event failed once and was offered again");
		var stored = await EventsAsync(nodeMaster.Events);
		await Assert.That(own.All(ev => stored.Any(kept => kept.EventId == ev.EventId))).IsTrue().Because("the master's log holds them all");
	}

	/// <summary>
	/// The refused events come from an earlier run's log: a write still unanswered when the page closed is answered on the next
	/// run, and its verdict reaches the listener there too.
	/// </summary>
	[Test]
	public async Task A_refused_write_of_an_earlier_run_gets_its_verdict_too()
	{
		var streamId = Guid.NewGuid();
		var dyingMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true, acknowledgeInbound: false);
		await dyingMaster.Started;
		var remembered = new RememberedState();
		var firstRun = new SynqraTestNode(streamId, builder => builder.Services.AddSingleton<IEventReplicationStateStore>(remembered)) { Port = dyingMaster.Port };
		await firstRun.Started;
		await WaitForOnlineAsync(firstRun);
		firstRun.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "written in an earlier run" });
		await WaitUntilAsync(() => Replication(firstRun).FramesSent >= 1, "the write is sent, and never answered");
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
		await Assert.That(stranded.OfType<ComponentAddedEvent>().Count()).IsEqualTo(1);
		await Assert.That(stranded.All(ev => verdicts[ev.EventId].Refusal is { Length: > 0 })).IsTrue();
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
