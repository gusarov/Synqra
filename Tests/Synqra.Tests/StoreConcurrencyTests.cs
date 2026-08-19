using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage.InMemory;
using Synqra.BinarySerializer;
using Synqra.Projection.InMemory;

namespace Synqra.Tests;

/// <summary>
/// A store is a process-wide singleton per stream, so "one thread writes while another reads" is the
/// ordinary shape of a hosted service ticking on a timer next to a request pipeline — not an exotic
/// case. Every collection a reader can reach therefore has to tolerate being written to mid-read.
/// <para>
/// Written as deterministic mid-enumeration mutations rather than as threaded races. The threaded
/// version of this suite reproduced the bug only most of the time — it is a race, so it can be lost —
/// and a test that fails intermittently is worse than no test. Holding an enumerator open and mutating
/// underneath it isolates the exact defect (<c>List&lt;T&gt;</c> invalidates every live enumerator on
/// write, throwing "Collection was modified; enumeration operation may not execute") and fails every
/// single run against the unguarded implementation.
/// </para>
/// </summary>
public class StoreConcurrencyTests
{
	static ServiceProvider BuildServices()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddTypeMetadataProvider(
			typeof(Command),
			typeof(ChangeObjectPropertyCommand),
			typeof(TestGraphNode),
			typeof(HierarchyLink)
		);
		services.AddSbxSerializer();
		InMemoryAppendStorageExtensions.AddAppendStorageInMemory<Event, Guid>(services, x => x.EventId);
		services.AddInMemorySynqraStore();
		return services.BuildServiceProvider();
	}

	static readonly Guid StreamId = new Guid("C0DE0000-0000-8000-9005-000000000100");

	static IObjectStore ResolveStore(IServiceProvider sp)
		=> (IObjectStore)sp.GetRequiredService<IProjectionProvider>().GetAsync(StreamId).GetAwaiter().GetResult();

	static TestGraphNode AddNode(IObjectStore store, string name)
	{
		var node = new TestGraphNode { Name = name };
		store.GetCollection<TestGraphNode>().Add(node);
		return node;
	}

	/// <summary>
	/// Walks <paramref name="items"/> to the end, invoking <paramref name="mutate"/> after the first
	/// step so the remainder of the walk runs against a collection that changed underneath it. Returns
	/// how many entries the enumerator yielded.
	/// </summary>
	static int EnumerateWhileMutating<T>(IEnumerable<T> items, Action mutate)
	{
		var seen = 0;
		using var enumerator = items.GetEnumerator();
		if (!enumerator.MoveNext())
		{
			throw new InvalidOperationException("The collection must be seeded before the mutation.");
		}
		seen++;
		mutate();
		while (enumerator.MoveNext())
		{
			seen++;
		}
		return seen;
	}

	[Test]
	public async Task Should_survive_a_write_to_a_store_collection_during_enumeration()
	{
		var sp = BuildServices();
		var store = ResolveStore(sp);
		for (var i = 0; i < 5; i++)
		{
			AddNode(store, $"seed-{i}");
		}

		var seen = EnumerateWhileMutating(
			store.GetCollection<TestGraphNode>(),
			() => AddNode(store, "written-mid-enumeration")
		);

		await Assert.That(seen).IsGreaterThanOrEqualTo(5);
		await Assert.That(store.GetCollection<TestGraphNode>().Count).IsEqualTo(6);
	}

	[Test]
	public async Task Should_survive_a_removal_from_a_store_collection_during_enumeration()
	{
		var sp = BuildServices();
		var store = ResolveStore(sp);
		var nodes = new List<TestGraphNode>();
		for (var i = 0; i < 5; i++)
		{
			nodes.Add(AddNode(store, $"seed-{i}"));
		}

		// RemoveByEvent is the projection's own apply path — the same one a delete event drives.
		var collection = (InMemoryStoreCollection)store.GetCollection<TestGraphNode>();
		var seen = EnumerateWhileMutating(
			store.GetCollection<TestGraphNode>(),
			() => collection.RemoveByEvent(nodes[4])
		);

		await Assert.That(seen).IsGreaterThan(0);
		await Assert.That(store.GetCollection<TestGraphNode>().Count).IsEqualTo(4);
	}

	[Test]
	public async Task Should_survive_a_component_attach_during_enumeration()
	{
		// ComponentsCollection is the structure every IComponentContainer holds, so covering it here
		// covers every node type without needing a store.
		var components = new ComponentsCollection();
		for (var i = 0; i < 5; i++)
		{
			components.TryAdd(new ProbeComponent());
		}

		var seen = EnumerateWhileMutating(components, () => components.TryAdd(new ProbeComponent()));

		await Assert.That(seen).IsGreaterThanOrEqualTo(5);
		await Assert.That(components.Count).IsEqualTo(6);
	}

	[Test]
	public async Task Should_survive_a_component_detach_during_enumeration()
	{
		var components = new ComponentsCollection();
		var attached = new List<IComponent>();
		for (var i = 0; i < 5; i++)
		{
			var c = new ProbeComponent();
			components.TryAdd(c);
			attached.Add(c);
		}

		var seen = EnumerateWhileMutating(components, () => components.BypassRemove(attached[4]));

		await Assert.That(seen).IsGreaterThan(0);
		await Assert.That(components.Count).IsEqualTo(4);
	}

	[Test]
	public async Task Should_survive_a_new_link_while_navigation_is_enumerated()
	{
		// Note this one passes even against the unguarded implementation: LinksAt already materialises
		// its result before returning, so a navigation read is walking a copy and cannot be invalidated
		// mid-enumeration. The link index's actual defect is the genuinely concurrent one — a plain
		// List<Link> being appended to by LinkAdded while LinksAt filters it — which no single-threaded
		// test can express. Kept as a behavioural guard on the rewritten index rather than as a repro.
		var sp = BuildServices();
		var store = ResolveStore(sp);
		var parent = AddNode(store, "parent");
		for (var i = 0; i < 5; i++)
		{
			parent.Children.Add(AddNode(store, $"seed-child-{i}"));
		}

		var seen = EnumerateWhileMutating(
			parent.Children,
			() => parent.Children.Add(AddNode(store, "linked-mid-enumeration"))
		);

		await Assert.That(seen).IsGreaterThan(0);
		await Assert.That(parent.Children.Count).IsEqualTo(6);
	}

	[Test]
	public async Task Should_keep_insertion_order_across_adds_and_removes()
	{
		// Order is load-bearing — callers reach for "the last one added" — so the concurrency fix must
		// not quietly turn these into unordered sets.
		var sp = BuildServices();
		var store = ResolveStore(sp);
		AddNode(store, "first");
		var second = AddNode(store, "second");
		AddNode(store, "third");

		var collection = (InMemoryStoreCollection)store.GetCollection<TestGraphNode>();
		collection.RemoveByEvent(second);
		AddNode(store, "fourth");

		var names = store.GetCollection<TestGraphNode>().Select(n => n.Name).ToArray();
		await Assert.That(names.Length).IsEqualTo(3);
		await Assert.That(names[0]).IsEqualTo("first");
		await Assert.That(names[1]).IsEqualTo("third");
		await Assert.That(names[2]).IsEqualTo("fourth");
	}
}

/// <summary>Payload-free component used only to exercise attach/detach in the tests above.</summary>
[Component]
public sealed class ProbeComponent : IComponent
{
}
