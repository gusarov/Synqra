using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.Projection.InMemory;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// A keeper catch-up and a local write race on the same projection (a page seeding its store while
/// another component borrows it): what the write applied must not be applied again from the log.
/// </summary>
public class ProjectionCatchUpTests : TestsStateManagement
{
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.Services.AddInMemorySynqraStore();
		hostApplicationBuilder.Services.AddSingleton<ParkingStorage>();
		hostApplicationBuilder.Services.AddSingleton<IAppendStorage<Event, (Guid, Guid)>>(sp => sp.GetRequiredService<ParkingStorage>());
		hostApplicationBuilder.Services.AddClientEventLog();
	}

	[Test]
	public async Task Should_not_apply_a_local_write_twice_when_it_lands_during_catch_up()
	{
		var storage = ServiceProvider.GetRequiredService<ParkingStorage>();
		var provider = ServiceProvider.GetRequiredService<IProjectionProvider>();
		var keeper = ServiceProvider.GetRequiredService<IProjectionKeeper>();
		var projection = await provider.GetAsync(Guid.NewGuid()); // cold hand-out over an empty log
		var store = (IObjectStore)projection;

		storage.Park();
		var catchUp = keeper.MaintainAsync(projection);
		await storage.Entered; // the catch-up has read its cursor and is opening the log

		var id = Guid.NewGuid();
		var write = WriteNodeWithUniqueComponentAsync(store, id);
		await Task.WhenAny(write, Task.Delay(300)); // lands now if nothing holds it back, else waits for the catch-up
		storage.Release();
		await catchUp;
		await write;

		var node = store.GetCollection<TestComponentNode>().Single(n => store.GetId(n) == id);
		await Assert.That(node.Components.Count).IsEqualTo(1);

		await keeper.MaintainAsync(projection); // a later catch-up finds nothing new
		await Assert.That(node.Components.Count).IsEqualTo(1);
	}

	/// <summary>
	/// Another author's event (a REST write, another device) carries that author's clock in its EventId, which can sort
	/// before everything this client already applied. The log stamps arrival order, so the catch-up still sees it.
	/// </summary>
	[Test]
	public async Task Should_apply_a_pushed_event_whose_id_sorts_before_the_cursor()
	{
		var log = ServiceProvider.GetRequiredService<IEventLog>();
		var provider = ServiceProvider.GetRequiredService<IProjectionProvider>();
		var keeper = ServiceProvider.GetRequiredService<IProjectionKeeper>();
		var streamId = Guid.NewGuid();
		var projection = await provider.GetAsync(streamId);
		var store = (IObjectStore)projection;
		await WriteNodeWithUniqueComponentAsync(store, Guid.NewGuid()); // the cursor moves past a local write

		var pushedId = Guid.NewGuid();
		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));
		await log.AppendAsync(new ComponentAddedEvent
		{
			EventId = new Guid("00000000-0000-7000-8000-000000000001"), // an early clock: sorts before every id this client minted
			CommandId = Guid.NewGuid(),
			StreamId = streamId,
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = pushedId,
			ComponentTypeId = container.TypeId,
			ComponentId = pushedId,
			Data = new TestComponentNode { Name = "pushed" },
		}); // as the replication service appends a received event
		await keeper.MaintainAsync(projection);

		await Assert.That(store.GetCollection<TestComponentNode>().Any(n => store.GetId(n) == pushedId)).IsTrue();
	}

	async Task WriteNodeWithUniqueComponentAsync(IObjectStore store, Guid id)
	{
		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));
		await store.SubmitCommandAsync(new AddComponentCommand
		{
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = id,
			ComponentTypeId = container.TypeId,
			ComponentId = id,
			Data = new TestComponentNode { Name = "container" },
		});
		await store.SubmitCommandAsync(new AddComponentCommand
		{
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = id,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestUniqueComponent)).TypeId,
			ComponentId = Guid.Empty, // unique components address by (container, type)
			Data = new TestUniqueComponent { Subject = "once" },
		});
	}

	/// <summary>An in-memory log whose enumeration can be held at the door, so a test can write while a catch-up is in flight.</summary>
	public sealed class ParkingStorage : IAppendStorage<Event, (Guid, Guid)>
	{
		readonly SortedDictionary<(Guid, Guid), Event> _data = new(Comparer<(Guid, Guid)>.Create((a, b) => a.Item1.CompareBigEndian(b.Item1) is var first && first != 0 ? first : a.Item2.CompareBigEndian(b.Item2)));
		TaskCompletionSource _released = Completed();
		TaskCompletionSource _entered = new();

		static TaskCompletionSource Completed()
		{
			var source = new TaskCompletionSource();
			source.SetResult();
			return source;
		}

		/// <summary>Completes when an enumeration has been requested and is waiting at the door.</summary>
		public Task Entered => _entered.Task;

		public void Park()
		{
			_released = new TaskCompletionSource();
			_entered = new TaskCompletionSource();
		}

		public void Release() => _released.TrySetResult();

		public Task AppendAsync(Event item, CancellationToken cancellationToken = default)
		{
			lock (_data)
			{
				_data[(item.StreamId, item.SequenceId)] = item;
			}
			return Task.CompletedTask;
		}

		public Task<Event> GetAsync((Guid, Guid) key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			lock (_data)
			{
				return Task.FromResult(_data[key]);
			}
		}

		public async IAsyncEnumerable<Event> GetAllAsync((Guid, Guid) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			_entered.TrySetResult();
			await _released.Task;
			Event[] snapshot;
			lock (_data)
			{
				snapshot = _data.Where(x => from == default || _data.Comparer.Compare(x.Key, from) > 0).Select(x => x.Value).ToArray();
			}
			foreach (var item in snapshot)
			{
				yield return item;
			}
		}

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
