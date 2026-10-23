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

	/// <summary>
	/// The log can hold what the projection cannot take: a facet this client added into a unique slot, and the master's own
	/// facet for the same slot pushed later (prod 2026-10-22: objects recreated server-side with the same ids). The catch-up
	/// steps over the refused event and applies what follows; stopping at it left every later event unapplied, on every
	/// catch-up and every page load.
	/// </summary>
	[Test]
	public async Task Should_step_over_an_event_the_projection_refuses_and_apply_the_rest()
	{
		var log = ServiceProvider.GetRequiredService<IEventLog>();
		var provider = ServiceProvider.GetRequiredService<IProjectionProvider>();
		var keeper = ServiceProvider.GetRequiredService<IProjectionKeeper>();
		var streamId = Guid.NewGuid();
		var projection = await provider.GetAsync(streamId);
		var store = (IObjectStore)projection;
		var id = Guid.NewGuid();
		await WriteNodeWithUniqueComponentAsync(store, id); // this client's own facet fills the unique slot

		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));
		await log.AppendAsync(new ComponentAddedEvent
		{
			EventId = Guid.NewGuid(), // the master's facet for the same slot, under its own event
			CommandId = Guid.NewGuid(),
			StreamId = streamId,
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = id,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestUniqueComponent)).TypeId,
			ComponentId = Guid.NewGuid(),
			Data = new TestUniqueComponent { Subject = "the master's" },
		});
		var laterId = Guid.NewGuid();
		await log.AppendAsync(new ComponentAddedEvent
		{
			EventId = Guid.NewGuid(),
			CommandId = Guid.NewGuid(),
			StreamId = streamId,
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = laterId,
			ComponentTypeId = container.TypeId,
			ComponentId = laterId,
			Data = new TestComponentNode { Name = "after the refused one" },
		});

		await keeper.MaintainAsync(projection);

		await Assert.That(store.GetCollection<TestComponentNode>().Any(n => store.GetId(n) == laterId)).IsTrue().Because("the event after the refused one is applied");
		var cursor = projection.Cursor;
		await keeper.MaintainAsync(projection);
		await Assert.That(projection.Cursor).IsEqualTo(cursor).Because("the refused event is stepped over for good, not retried");
	}

	/// <summary>
	/// A component whose container this client never got (its create fell into a replay hole) is refused by the fold. The
	/// catch-up steps over it and still applies a later event: prod 2026-10-22 threw "Container ... not found" on every load
	/// and nothing after it, live pushes included, was ever folded.
	/// </summary>
	[Test]
	public async Task Should_apply_events_after_a_component_whose_container_is_missing()
	{
		var log = ServiceProvider.GetRequiredService<IEventLog>();
		var provider = ServiceProvider.GetRequiredService<IProjectionProvider>();
		var keeper = ServiceProvider.GetRequiredService<IProjectionKeeper>();
		var streamId = Guid.NewGuid();
		var projection = await provider.GetAsync(streamId);
		var store = (IObjectStore)projection;
		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));

		await log.AppendAsync(new ComponentAddedEvent
		{
			EventId = Guid.NewGuid(),
			CommandId = Guid.NewGuid(),
			StreamId = streamId,
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = Guid.NewGuid(), // a container this log never held
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestUniqueComponent)).TypeId,
			ComponentId = Guid.NewGuid(),
			Data = new TestUniqueComponent { Subject = "orphan" },
		});
		var laterId = Guid.NewGuid();
		await log.AppendAsync(new ComponentAddedEvent
		{
			EventId = Guid.NewGuid(),
			CommandId = Guid.NewGuid(),
			StreamId = streamId,
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = laterId,
			ComponentTypeId = container.TypeId,
			ComponentId = laterId,
			Data = new TestComponentNode { Name = "pushed after the orphan" },
		});

		await keeper.MaintainAsync(projection);

		await Assert.That(store.GetCollection<TestComponentNode>().Any(n => store.GetId(n) == laterId)).IsTrue().Because("the event after the orphan is applied");
	}

	/// <summary>
	/// A root added again for an entity already here: a device that seeded a deterministic id before its replay caught up
	/// (pr.quotaly.com 2026-10-23, the stream's own thing). The server keeps the entity it has; so does the fold, once,
	/// with what was added to it. It threw "This id is already used", which no catch-up steps over, and every fresh
	/// replay of the stream stopped there: the page never opened.
	/// </summary>
	[Test]
	public async Task Should_keep_an_entity_whose_root_is_added_again()
	{
		var log = ServiceProvider.GetRequiredService<IEventLog>();
		var provider = ServiceProvider.GetRequiredService<IProjectionProvider>();
		var keeper = ServiceProvider.GetRequiredService<IProjectionKeeper>();
		var streamId = Guid.NewGuid();
		var projection = await provider.GetAsync(streamId);
		var store = (IObjectStore)projection;
		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));
		var id = Guid.NewGuid();
		var laterId = Guid.NewGuid();

		await log.AppendAsync(BuildRootAdd(store, streamId, id, "first"));
		await log.AppendAsync(new ComponentAddedEvent
		{
			EventId = Guid.NewGuid(),
			CommandId = Guid.NewGuid(),
			StreamId = streamId,
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = id,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestUniqueComponent)).TypeId,
			ComponentId = Guid.NewGuid(),
			Data = new TestUniqueComponent { Subject = "kept" },
		});
		await log.AppendAsync(BuildRootAdd(store, streamId, id, "seeded again"));
		await log.AppendAsync(BuildRootAdd(store, streamId, laterId, "after it"));

		await keeper.MaintainAsync(projection);

		var nodes = store.GetCollection<TestComponentNode>().Where(n => store.GetId(n) == id).ToList();
		await Assert.That(nodes.Count).IsEqualTo(1).Because("the entity is listed once");
		await Assert.That(nodes[0].Components.Count).IsEqualTo(1).Because("what was added to it stays");
		await Assert.That(store.GetCollection<TestComponentNode>().Any(n => store.GetId(n) == laterId)).IsTrue().Because("the fold goes on past it");
	}

	ComponentAddedEvent BuildRootAdd(IObjectStore store, Guid streamId, Guid id, string name)
	{
		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));
		return new ComponentAddedEvent
		{
			EventId = Guid.NewGuid(),
			CommandId = Guid.NewGuid(),
			StreamId = streamId,
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = id,
			ComponentTypeId = container.TypeId,
			ComponentId = id,
			Data = new TestComponentNode { Name = name },
		};
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
