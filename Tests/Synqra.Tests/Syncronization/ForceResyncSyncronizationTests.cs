using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.Simulator;
using Synqra.Tests.TestHelpers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// A Force Resync empties the client's local log and its cursors, so the next start replays the whole stream. It used to
/// keep the log (the event log the client holds did not pass the wipe on to its storage) and, while the page ran on, to
/// persist a server watermark again: the reload resumed near the tail and a hole in the log was never filled (WI 487).
/// </summary>
[NotInParallel]
internal class ForceResyncSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;

	[Test]
	public async Task A_force_resync_empties_the_local_log_and_its_cursors()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await nodeMaster.Started;
		var log = new ClearableLog();
		var remembered = new RememberingStateStore();
		var nodeA = new SynqraTestNode(streamId, builder =>
		{
			builder.Services.AddSingleton<IAppendStorage<Event, (Guid, Guid)>>(log);
			builder.Services.AddSingleton<IEventReplicationStateStore>(remembered);
		}) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);
		nodeA.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "before the resync" });
		await WaitUntilAsync(() => Task.FromResult(remembered.Saved?.LastEventIdFromMe != default), "the write is acknowledged");

		await nodeA.Host.Services.GetRequiredService<IEventReplicationService>().ClearLocalStorageAsync();

		await Assert.That(log.Count).IsEqualTo(0).Because("the local log is wiped, not only the cursors");
		await Assert.That(remembered.Saved!.ServerWatermark).IsEqualTo(default(Guid));
		await Assert.That(remembered.Saved!.LastEventIdFromMe).IsEqualTo(default(Guid));
	}

	[Test]
	public async Task Nothing_received_after_a_force_resync_is_stored()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await nodeMaster.Started;
		var log = new ClearableLog();
		var remembered = new RememberingStateStore();
		var nodeA = new SynqraTestNode(streamId, builder =>
		{
			builder.Services.AddSingleton<IAppendStorage<Event, (Guid, Guid)>>(log);
			builder.Services.AddSingleton<IEventReplicationStateStore>(remembered);
		}) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitForOnlineAsync(nodeA);
		var nodeB = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeB.Started;
		await WaitForOnlineAsync(nodeB);

		await nodeA.Host.Services.GetRequiredService<IEventReplicationService>().ClearLocalStorageAsync();
		nodeB.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "written while A reloads" });
		await WaitUntilAsync(async () => await CountAsync(nodeMaster.Events) > 0, "B's write reaches the master");
		await Task.Delay(1000); // a push to A would have landed by now

		await Assert.That(log.Count).IsEqualTo(0).Because("the run ended with the wipe: the next start replays the stream");
		await Assert.That(remembered.Saved!.ServerWatermark).IsEqualTo(default(Guid)).Because("no watermark is persisted after the wipe");
	}

	[Test]
	public async Task A_log_passes_the_wipe_on_to_its_storage()
	{
		var stored = new ClearableLog();
		var log = new ClientEventLog(stored, new EventSequencer(Microsoft.Extensions.Options.Options.Create(new EventSequencerOptions())));
		var streamId = Guid.NewGuid();
		await log.AppendAsync(new ObjectDeletedEvent { EventId = Guid.CreateVersion7(), CommandId = Guid.NewGuid(), StreamId = streamId, TargetId = Guid.NewGuid(), TargetTypeId = Guid.NewGuid(), CollectionId = Guid.NewGuid() });

		var clearable = (object)log as IClearableAppendStorage;
		await Assert.That(clearable).IsNotNull().Because("the log a client holds must pass a wipe on");
		await clearable!.ClearAllAsync();

		await Assert.That(stored.Count).IsEqualTo(0);
	}

	[Test]
	public async Task A_log_over_a_durable_store_refuses_the_wipe()
	{
		var log = new ServerEventLog(new DurableLog(), new EventSequencer(Microsoft.Extensions.Options.Options.Create(new EventSequencerOptions())));

		var clearable = (object)log as IClearableAppendStorage;
		await Assert.That(clearable).IsNotNull();
		await Assert.ThrowsAsync<NotSupportedException>(async () => await clearable!.ClearAllAsync());
	}

	async Task<int> CountAsync(IEventLog log)
	{
		var count = 0;
		await foreach (var _ in log.GetAllAsync())
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

	/// <summary>The host's state store, in memory: what a client saved is what its next run loads.</summary>
	sealed class RememberingStateStore : IEventReplicationStateStore
	{
		public EventReplicationState? Saved { get; private set; }

		public Task<EventReplicationState?> LoadAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(Saved is null ? null : Copy(Saved));

		public Task SaveAsync(Guid streamId, EventReplicationState state, CancellationToken cancellationToken = default)
		{
			Saved = Copy(state);
			return Task.CompletedTask;
		}

		EventReplicationState Copy(EventReplicationState state) => new() { MyNodeId = state.MyNodeId, LastEventIdFromMe = state.LastEventIdFromMe, ServerWatermark = state.ServerWatermark };
	}

	/// <summary>A client's local log in memory that can be wiped, as the browser's IndexedDB one can.</summary>
	sealed class ClearableLog : DurableLog, IClearableAppendStorage
	{
		public Task ClearAllAsync(CancellationToken cancellationToken = default)
		{
			lock (Events)
			{
				Events.Clear();
			}
			return Task.CompletedTask;
		}
	}

	/// <summary>A log keyed like a client's, in memory, with no way to wipe it.</summary>
	class DurableLog : IAppendStorage<Event, (Guid, Guid)>
	{
		protected SortedDictionary<(Guid, Guid), Event> Events { get; } = new(Comparer<(Guid, Guid)>.Create((a, b) => a.Item1.CompareBigEndian(b.Item1) is var first && first != 0 ? first : a.Item2.CompareBigEndian(b.Item2)));

		public int Count
		{
			get
			{
				lock (Events)
				{
					return Events.Count;
				}
			}
		}

		public Task AppendAsync(Event item, CancellationToken cancellationToken = default)
		{
			lock (Events)
			{
				Events.Add((item.StreamId, item.SequenceId), item);
			}
			return Task.CompletedTask;
		}

		public Task<Event> GetAsync((Guid, Guid) key, CancellationToken cancellationToken = default)
		{
			lock (Events)
			{
				return Task.FromResult(Events[key]);
			}
		}

		public async IAsyncEnumerable<Event> GetAllAsync((Guid, Guid) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			List<Event> after;
			lock (Events)
			{
				after = [.. Events.Where(e => from == default || Events.Comparer.Compare(e.Key, from) > 0).Select(e => e.Value)];
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
}
