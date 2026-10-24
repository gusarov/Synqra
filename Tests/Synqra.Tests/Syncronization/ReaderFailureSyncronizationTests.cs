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
/// A frame the client fails to take ends the connection, not replication. The reader was an unobserved task: its exception
/// vanished, IsOnline stayed true and nothing was received again until the page reloaded (dev 2026-10-23: the cursor stopped
/// one frame short of an MCP batch, with only a stackless first-chance exception in the console).
/// </summary>
internal class ReaderFailureSyncronizationTests : BaseTest
{
	const int PropagationTimeoutMs = 30_000;

	[Test]
	public async Task A_frame_the_client_fails_to_take_is_received_again_after_a_reconnect()
	{
		var streamId = Guid.NewGuid();
		var nodeMaster = new SynqraTestNode(streamId, builder => { }, masterHost: true);
		await nodeMaster.Started;
		var log = new FailingOnceLog();
		var nodeA = new SynqraTestNode(streamId, builder =>
		{
			builder.Services.AddSingleton<IAppendStorage<Event, (Guid, Guid)>>(log);
		}) { Port = nodeMaster.Port };
		await nodeA.Started;
		await WaitUntilAsync(() => Task.FromResult(nodeA.StoreContext.IsOnline()), "A is online");
		var nodeB = new SynqraTestNode(streamId, builder => { }) { Port = nodeMaster.Port };
		await nodeB.Started;
		await WaitUntilAsync(() => Task.FromResult(nodeB.StoreContext.IsOnline()), "B is online");

		log.FailNext();
		nodeB.StoreContext.GetCollection<SampleTaskModel>().Add(new SampleTaskModel { Subject = "written while A's reader fails" });

		await WaitUntilAsync(() => Task.FromResult(log.Failed), "A's log refused the pushed event once");
		await WaitUntilAsync(() => Task.FromResult(log.Count > 0), "A receives the event again over a new connection");
		await WaitUntilAsync(() => Task.FromResult(nodeA.StoreContext.IsOnline()), "A is online again");
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

	/// <summary>A client's log in memory that refuses one append when told to, as a frame the client cannot take.</summary>
	sealed class FailingOnceLog : IAppendStorage<Event, (Guid, Guid)>
	{
		readonly SortedDictionary<(Guid, Guid), Event> _events = new(Comparer<(Guid, Guid)>.Create((a, b) => a.Item1.CompareBigEndian(b.Item1) is var first && first != 0 ? first : a.Item2.CompareBigEndian(b.Item2)));
		volatile bool _failNext;

		public bool Failed { get; private set; }

		public int Count
		{
			get
			{
				lock (_events)
				{
					return _events.Count;
				}
			}
		}

		public void FailNext() => _failNext = true;

		public Task AppendAsync(Event item, CancellationToken cancellationToken = default)
		{
			if (_failNext)
			{
				_failNext = false;
				Failed = true;
				throw new IndexOutOfRangeException("a frame this client cannot take");
			}
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

		public async IAsyncEnumerable<Event> GetAllAsync((Guid, Guid) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			List<Event> after;
			lock (_events)
			{
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
}
