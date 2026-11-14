using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TUnit.Assertions.Extensions;
#if NET10_0_OR_GREATER
using Synqra.Replication.AspNetCore;
#endif

namespace Synqra.Tests.Syncronization;

/// <summary>
/// A client's replication never looks dead (prod 2026-11: a browser stayed on "Syncing your space · 606 events" forever, the
/// master's CaughtUp never came, and after a server restart the client waited up to 30 s to try again).
/// </summary>
internal class ReconnectSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;

	[Test]
	public async Task Should_pause_half_a_second_doubling_to_at_most_eight_seconds()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true) { RefusesConnections = true };
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		try
		{
			var replication = nodeA.Host.Services.GetRequiredService<EventReplicationService>();

			var pauses = Enumerable.Range(0, 12).Select(replication.CalculateBackoff).Select(x => x.TotalMilliseconds).ToArray();

			await Assert.That(pauses.SequenceEqual([500d, 1_000, 2_000, 4_000, 8_000, 8_000, 8_000, 8_000, 8_000, 8_000, 8_000, 8_000])).IsTrue().Because(string.Join(", ", pauses));
		}
		finally
		{
			await StopAsync(nodeA, nodeMaster);
		}
	}

	[Test]
	public async Task Should_connect_at_once_when_asked_during_a_pause()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true) { RefusesConnections = true };
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		try
		{
			var replication = nodeA.Host.Services.GetRequiredService<EventReplicationService>();
			var announced = false;
			replication.StatusChanged += () => announced |= replication.NextAttemptAt is not null;
			await WaitUntilAsync(() => replication.NextAttemptAt is { } at && at - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(2.5), "A pauses seconds before its next attempt");
			var due = replication.NextAttemptAt!.Value;

			nodeMaster.RefusesConnections = false; // the server is back
			replication.ReconnectNow();
			await WaitUntilAsync(() => replication.IsOnline, "A is online");

			await Assert.That(DateTimeOffset.UtcNow < due).IsTrue().Because($"A connected before its pause ({due:O}) was over");
			await Assert.That(replication.NextAttemptAt is null).IsTrue();
			await Assert.That(announced).IsTrue().Because("each pause raised StatusChanged with its due time");
		}
		finally
		{
			await StopAsync(nodeA, nodeMaster);
		}
	}

	[Test]
	public async Task Should_leave_a_caught_up_connection_alone_when_asked_to_reconnect()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		try
		{
			var replication = nodeA.Host.Services.GetRequiredService<EventReplicationService>();
			await WaitUntilAsync(() => replication.IsCaughtUp, "A is caught up");

			replication.ReconnectNow();
			await Task.Delay(1_500); // a reconnect would show by now

			await Assert.That(nodeMaster.Connections).IsEqualTo(1);
			await Assert.That(replication.IsCaughtUp).IsTrue();
		}
		finally
		{
			await StopAsync(nodeA, nodeMaster);
		}
	}

	[Test]
	public async Task Should_cut_a_connection_whose_catch_up_is_not_in_when_asked_to_reconnect()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true) { AnswersCatchUp = false };
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port, CatchUpTimeout = TimeSpan.FromMinutes(10) };
		await nodeA.Started;
		try
		{
			var replication = nodeA.Host.Services.GetRequiredService<EventReplicationService>();
			await WaitUntilAsync(() => replication.IsOnline, "A is online");
			var asked = Stopwatch.StartNew();

			replication.ReconnectNow();
			await WaitUntilAsync(() => nodeMaster.Connections >= 2, "A connects again");

			await Assert.That(asked.Elapsed).IsLessThan(TimeSpan.FromSeconds(5)).Because("no pause before the attempt asked for");
		}
		finally
		{
			await StopAsync(nodeA, nodeMaster);
		}
	}

	/// <summary>A master that never answers the catch-up probe: the client takes the silent connection for dead and connects again.</summary>
	[Test]
	public async Task Should_reconnect_when_the_catch_up_never_ends()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true) { AnswersCatchUp = false };
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port, CatchUpTimeout = TimeSpan.FromSeconds(1) };
		await nodeA.Started;
		try
		{
			var replication = nodeA.Host.Services.GetRequiredService<EventReplicationService>();
			await WaitUntilAsync(() => nodeMaster.Connections >= 3, "A drops the silent connection and connects again, twice");
			await Assert.That(replication.IsCaughtUp).IsFalse();

			nodeMaster.AnswersCatchUp = true;
			await WaitUntilAsync(() => replication.IsCaughtUp, "the next connection catches up");
			var connections = nodeMaster.Connections;
			await Task.Delay(2_500); // past the watchdog's second: a caught-up connection is never cut

			await Assert.That(nodeMaster.Connections).IsEqualTo(connections);
			await Assert.That(replication.IsOnline).IsTrue();
		}
		finally
		{
			await StopAsync(nodeA, nodeMaster);
		}
	}

#if NET10_0_OR_GREATER
	/// <summary>A master whose backlog read hangs (a Mongo getMore) aborts the connection instead of holding it open in silence.</summary>
	[Test]
	public async Task Should_abort_the_connection_when_the_backlog_read_hangs()
	{
		var streamId = Guid.NewGuid();
		var log = new StallingLog();
		var nodeMaster = new SynqraTestNode(streamId, builder =>
		{
			builder.Services.AddSingleton<IAppendStorage<Event, (Guid, Guid)>>(log);
			builder.Services.Configure<SynqraReplicationEndpointOptions>(o => o.BacklogReadTimeout = TimeSpan.FromSeconds(1));
		}, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		// The client's own watchdog is off: only the master can end the connection here.
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port, CatchUpTimeout = TimeSpan.Zero };
		await nodeA.Started;
		try
		{
			var replication = nodeA.Host.Services.GetRequiredService<EventReplicationService>();
			await WaitUntilAsync(() => log.Reads >= 2, "the master aborts the hung connection and A's next one reads the backlog again");
			await Assert.That(replication.IsCaughtUp).IsFalse();

			log.Stalls = false;
			await WaitUntilAsync(() => replication.IsCaughtUp, "with the read answering again A catches up");
		}
		finally
		{
			await StopAsync(nodeA, nodeMaster);
		}
	}

	/// <summary>A master's log whose reads hang while it stalls; it knows its tail without a read, so only the backlog reads it.</summary>
	sealed class StallingLog : IAppendStorage<Event, (Guid, Guid)>, IEventSequenceTail
	{
		int _reads;
		volatile bool _stalls = true;

		public bool Stalls
		{
			get => _stalls;
			set => _stalls = value;
		}

		public int Reads => Volatile.Read(ref _reads);

		public Task AppendAsync(Event item, CancellationToken cancellationToken = default) => throw new NotSupportedException("this master is only read");

		public Task<Event> GetAsync((Guid, Guid) key, CancellationToken cancellationToken = default) => throw new NotSupportedException("this master is only read");

		public async IAsyncEnumerable<Event> GetAllAsync((Guid, Guid) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _reads);
			if (Stalls)
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}
			yield break; // the stream holds nothing: the read either hangs or ends
		}

		public Task<Guid> LastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(Guid.Empty);

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
#endif

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
