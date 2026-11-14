using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synqra.Tests.SampleModels.Syncronization;
using System.Collections.Concurrent;
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
	public async Task Should_pause_half_a_second_doubling_to_at_most_eight_seconds_within_a_fifth()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true) { RefusesConnections = true };
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		try
		{
			var replication = nodeA.Host.Services.GetRequiredService<EventReplicationService>();

			double[] middles = [500, 1_000, 2_000, 4_000, 8_000, 8_000, 8_000, 8_000, 8_000, 8_000, 8_000, 8_000];

			for (var round = 0; round < 50; round++)
			{
				var pauses = Enumerable.Range(0, middles.Length).Select(replication.CalculateBackoff).Select(x => x.TotalMilliseconds).ToArray();
				var within = pauses.Zip(middles, (pause, middle) => pause >= middle * 0.8 && pause <= Math.Min(middle * 1.2, 8_000)).All(x => x);
				await Assert.That(within).IsTrue().Because(string.Join(", ", pauses));
			}
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
	public async Task Should_cut_a_catch_up_gone_silent_when_asked_to_reconnect()
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
			await Task.Delay(2_500); // nothing more comes: the catch-up has gone silent
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

	/// <summary>A catch-up that keeps receiving frames is not cut: a host asks on every tab return, and a snapshot would be counted as lost.</summary>
	[Test]
	public async Task Should_leave_a_catch_up_that_keeps_receiving_alone_when_asked_to_reconnect()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeA.Started;
		SynqraTestNode? nodeC = null;
		try
		{
			for (var i = 0; i < 10; i++)
			{
				nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = $"task {i}" });
			}
			await WaitUntilAsync(() => nodeA.Host.Services.GetRequiredService<EventReplicationService>().FramesSent >= 10, "A's writes are sent");
			await Task.Delay(500); // and applied by the master
			nodeMaster.BacklogPause = TimeSpan.FromMilliseconds(300);

			nodeC = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
			await nodeC.Started;
			var replication = nodeC.Host.Services.GetRequiredService<EventReplicationService>();
			await WaitUntilAsync(() => replication.ConnectionFrames >= 2, "C's catch-up is under way");
			replication.ReconnectNow();
			replication.ReconnectNow();
			await WaitUntilAsync(() => replication.IsCaughtUp, "C is caught up");

			await Assert.That(nodeMaster.Connections).IsEqualTo(2).Because("A's and C's first connections, neither cut");
		}
		finally
		{
			if (nodeC is not null)
			{
				await StopAsync(nodeC);
			}
			await StopAsync(nodeA, nodeMaster);
		}
	}

	/// <summary>A cold client's snapshot that never starts: two watchdog cuts, and the next HELLO asks for the replay instead.</summary>
	[Test]
	public async Task Should_replay_instead_after_two_cuts_waiting_for_a_snapshot()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true) { AnswersCatchUp = false };
		await nodeMaster.Started;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port, HelloKind = ReplicationHelloKind.SubscribeTo, CatchUpTimeout = TimeSpan.FromSeconds(1) };
		await nodeA.Started;
		try
		{
			await WaitUntilAsync(() => nodeMaster.HelloKinds.Count >= 3, "A connected three times");

			await Assert.That(nodeMaster.HelloKinds.Take(3).ToArray().SequenceEqual([ReplicationHelloKind.SubscribeToSnapshot, ReplicationHelloKind.SubscribeToSnapshot, ReplicationHelloKind.SubscribeTo])).IsTrue().Because(string.Join(", ", nodeMaster.HelloKinds));
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
		await nodeMaster.Started; // its own projection read the log at start: the stall begins after it
		log.Stalls = true;
		var readsBefore = log.Reads;
		// The client's own watchdog is off: only the master can end the connection here.
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port, CatchUpTimeout = TimeSpan.Zero };
		await nodeA.Started;
		try
		{
			var replication = nodeA.Host.Services.GetRequiredService<EventReplicationService>();
			await WaitUntilAsync(() => log.Reads >= readsBefore + 2, "the master aborts the hung connection and A's next one reads the backlog again");
			await Assert.That(replication.IsCaughtUp).IsFalse();

			log.Stalls = false;
			await WaitUntilAsync(() => replication.IsCaughtUp, "with the read answering again A catches up");
		}
		finally
		{
			await StopAsync(nodeA, nodeMaster);
		}
	}

	/// <summary>A hung read that ignores its cancellation: the handler leaves it behind and ends, and the client still gets through.</summary>
	[Test]
	public async Task Should_end_the_handler_when_the_hung_backlog_read_ignores_its_cancellation()
	{
		var streamId = Guid.NewGuid();
		var log = new StallingLog { IgnoresCancellation = true };
		var logs = new CapturingLoggerProvider();
		var nodeMaster = new SynqraTestNode(streamId, builder =>
		{
			builder.Services.AddSingleton<IAppendStorage<Event, (Guid, Guid)>>(log);
			builder.Services.Configure<SynqraReplicationEndpointOptions>(o => o.BacklogReadTimeout = TimeSpan.FromSeconds(1));
			builder.Logging.AddProvider(logs);
		}, masterHost: true, useRealEndpoint: true);
		await nodeMaster.Started;
		log.Stalls = true;
		var readsBefore = log.Reads;
		var nodeA = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port, CatchUpTimeout = TimeSpan.Zero };
		await nodeA.Started;
		try
		{
			var replication = nodeA.Host.Services.GetRequiredService<EventReplicationService>();
			await WaitUntilAsync(() => log.Reads >= readsBefore + 2, "the master gives up on the hung read and the next connection of A reads again");
			await WaitUntilAsync(() => logs.Contains("orphaned"), "the handler left the hung read behind and ended");

			log.Stalls = false;
			await WaitUntilAsync(() => replication.IsCaughtUp, "with the read answering again A catches up");
		}
		finally
		{
			log.Release();
			await StopAsync(nodeA, nodeMaster);
		}
	}

	/// <summary>Keeps what the host logs, to look for a message.</summary>
	sealed class CapturingLoggerProvider : ILoggerProvider
	{
		readonly ConcurrentQueue<string> _messages = new ConcurrentQueue<string>();

		public bool Contains(string text) => _messages.Any(x => x.Contains(text, StringComparison.Ordinal));

		public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

		public void Dispose()
		{
		}

		sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> messages.Enqueue(formatter(state, exception));
		}
	}

	/// <summary>A master's log whose reads hang while it stalls; it knows its tail without a read, so once the host is up only a backlog reads it.</summary>
	sealed class StallingLog : IAppendStorage<Event, (Guid, Guid)>, IEventSequenceTail
	{
		int _reads;
		volatile bool _stalls;
		readonly TaskCompletionSource _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>A hung read waits for <see cref="Release"/> alone, deaf to its cancellation, as a store that ignores it.</summary>
		public bool IgnoresCancellation { get; init; }

		/// <summary>Lets every read that ignores its cancellation end.</summary>
		public void Release() => _released.TrySetResult();

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
				await (IgnoresCancellation ? _released.Task : Task.Delay(Timeout.Infinite, cancellationToken));
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
