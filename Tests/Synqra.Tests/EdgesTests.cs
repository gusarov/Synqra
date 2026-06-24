using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synqra.BinarySerializer;
using Synqra.Projection.InMemory;

namespace Synqra.Tests;

// ---- Sample edge/node models exercising every flavour the Todo solution's
// IHierarchy/IMHierarchy/IMRHierarchy/IDependable family hand-rolled separately
// (see plans/edges.md). One framework primitive (Edge/DirectedEdge/UndirectedEdge),
// differentiated by concrete type, replaces all four.

/// <summary>Generic parent→child edge. No uniqueness constraint in v1, so the same
/// type serves both "single parent" (IHierarchy) and "multi parent" (IMHierarchy)
/// callers — the difference is purely how many a caller happens to create. Carries
/// <see cref="Order"/> as reified payload (IMRHierarchy's "relation has its own data").</summary>
[SynqraModel]
[Schema(2026.420, "1 AObjectId Guid AComponentTypeId Guid AComponentId Guid BObjectId Guid BComponentTypeId Guid BComponentId Guid Order double?")]
public partial class HierarchyEdge : DirectedEdge
{
	public partial double? Order { get; set; }
}

/// <summary>Pure marker edge (IDependable's "blocks/blockedBy"). Adds no fields of its
/// own, so — like <see cref="ObjectDeletedEvent"/> — it is a plain subclass.</summary>
public sealed class DependsOn : DirectedEdge
{
}

/// <summary>Undirected relation — absent from the Todo predecessor, new in this design.</summary>
[SynqraModel]
[Schema(2026.421, "1 AObjectId Guid AComponentTypeId Guid AComponentId Guid BObjectId Guid BComponentTypeId Guid BComponentId Guid Label string?")]
public partial class RelatedTo : UndirectedEdge
{
	public partial string? Label { get; set; }
}

[SynqraModel]
[Schema(2026.422, "1 Name string?")]
public partial class TestGraphNode
{
	public partial string? Name { get; set; }

	// The "simple Parent property" — one line each, no generated/hand-rolled id lists.
	public TestGraphNode? Parent => EdgeNav.SingleParent<TestGraphNode, HierarchyEdge>(this);
	public IReadOnlyList<TestGraphNode> Parents => EdgeNav.Parents<TestGraphNode, HierarchyEdge>(this);
	public IReadOnlyList<TestGraphNode> Children => EdgeNav.Children<TestGraphNode, HierarchyEdge>(this);
	public IReadOnlyList<TestGraphNode> BlockedBy => EdgeNav.Parents<TestGraphNode, DependsOn>(this);
	public IReadOnlyList<TestGraphNode> Blocks => EdgeNav.Children<TestGraphNode, DependsOn>(this);
	public IReadOnlyList<TestGraphNode> RelatedNodes => EdgeNav.Related<TestGraphNode, RelatedTo>(this);
}

public class EdgesTests
{
	static ServiceProvider BuildServices()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddTypeMetadataProvider(
			typeof(Command),
			typeof(CreateObjectCommand),
			typeof(ChangeObjectPropertyCommand),
			typeof(TestGraphNode),
			typeof(HierarchyEdge),
			typeof(DependsOn),
			typeof(RelatedTo)
		);
		services.AddSbxSerializer();
		services.AddInMemorySynqraStore();
		return services.BuildServiceProvider();
	}

	static TestGraphNode AddNode(IObjectStore store, string name)
	{
		var node = new TestGraphNode { Name = name };
		store.GetCollection<TestGraphNode>().Add(node);
		return node;
	}

	static Ref RefOf(IObjectStore store, object obj) => new(store.GetId(obj));

	// ---- Flavour: IHierarchy (single parent) ----

	[Test]
	public async Task Should_navigate_single_parent_and_children()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");
		store.GetCollection<HierarchyEdge>().Add(new HierarchyEdge { AObjectId = RefOf(store, parent).ObjectId, BObjectId = RefOf(store, child).ObjectId });

		await Assert.That(child.Parent).IsNotNull();
		await Assert.That(store.GetId(child.Parent!)).IsEqualTo(store.GetId(parent));
		await Assert.That(parent.Children.Count).IsEqualTo(1);
		await Assert.That(store.GetId(parent.Children[0])).IsEqualTo(store.GetId(child));
	}

	// ---- Flavour: IMHierarchy (multi-parent — same edge type, no constraint) ----

	[Test]
	public async Task Should_support_multiple_parents_via_the_same_edge_type()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parentA = AddNode(store, "parentA");
		var parentB = AddNode(store, "parentB");
		var child = AddNode(store, "child");
		store.GetCollection<HierarchyEdge>().Add(new HierarchyEdge { AObjectId = RefOf(store, parentA).ObjectId, BObjectId = RefOf(store, child).ObjectId });
		store.GetCollection<HierarchyEdge>().Add(new HierarchyEdge { AObjectId = RefOf(store, parentB).ObjectId, BObjectId = RefOf(store, child).ObjectId });

		await Assert.That(child.Parents.Count).IsEqualTo(2);
		// SingleParent degrades to "first" once there's more than one — multi-parent callers use Parents, not Parent.
		await Assert.That(child.Parent).IsNotNull();
	}

	// ---- Flavour: IMRHierarchy (the relation carries its own data) ----

	[Test]
	public async Task Should_carry_payload_on_the_edge_itself()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");
		var edges = store.GetCollection<HierarchyEdge>();
		edges.Add(new HierarchyEdge { AObjectId = RefOf(store, parent).ObjectId, BObjectId = RefOf(store, child).ObjectId, Order = 2.5 });

		var stored = edges.Single();
		await Assert.That(stored.Order).IsEqualTo(2.5);
	}

	// ---- Flavour: IDependable (a different edge type between the same endpoints coexists) ----

	[Test]
	public async Task Should_differentiate_coexisting_edge_types_between_the_same_endpoints()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();
		var index = (IEdgeIndex)store;

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		store.GetCollection<HierarchyEdge>().Add(new HierarchyEdge { AObjectId = RefOf(store, a).ObjectId, BObjectId = RefOf(store, b).ObjectId });
		store.GetCollection<DependsOn>().Add(new DependsOn { AObjectId = RefOf(store, a).ObjectId, BObjectId = RefOf(store, b).ObjectId });

		var between = index.EdgesBetween(RefOf(store, a), RefOf(store, b));
		await Assert.That(between.Count).IsEqualTo(2);
		await Assert.That(between.OfType<HierarchyEdge>().Count()).IsEqualTo(1);
		await Assert.That(between.OfType<DependsOn>().Count()).IsEqualTo(1);

		await Assert.That(b.BlockedBy.Count).IsEqualTo(1);
		await Assert.That(a.Blocks.Count).IsEqualTo(1);
	}

	// ---- Structural dedup ----

	[Test]
	public async Task Should_reject_a_structurally_duplicate_directed_edge()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		store.GetCollection<HierarchyEdge>().Add(new HierarchyEdge { AObjectId = RefOf(store, a).ObjectId, BObjectId = RefOf(store, b).ObjectId });

		var ex = await Assert.ThrowsAsync(async () =>
		{
			store.GetCollection<HierarchyEdge>().Add(new HierarchyEdge { AObjectId = RefOf(store, a).ObjectId, BObjectId = RefOf(store, b).ObjectId });
		});
		await Assert.That(ex is InvalidOperationException).IsTrue();
	}

	[Test]
	public async Task Should_allow_the_reverse_direction_of_a_directed_edge_as_distinct()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		store.GetCollection<HierarchyEdge>().Add(new HierarchyEdge { AObjectId = RefOf(store, a).ObjectId, BObjectId = RefOf(store, b).ObjectId });
		// B -> A is a different structural key for a directed edge, so it must be accepted.
		store.GetCollection<HierarchyEdge>().Add(new HierarchyEdge { AObjectId = RefOf(store, b).ObjectId, BObjectId = RefOf(store, a).ObjectId });

		await Assert.That(store.GetCollection<HierarchyEdge>().Count).IsEqualTo(2);
	}

	// ---- Undirected: order-independent structural key ----

	[Test]
	public async Task Should_treat_undirected_edge_as_incident_from_either_endpoint()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		store.GetCollection<RelatedTo>().Add(new RelatedTo { AObjectId = RefOf(store, a).ObjectId, BObjectId = RefOf(store, b).ObjectId, Label = "friends" });

		await Assert.That(a.RelatedNodes.Count).IsEqualTo(1);
		await Assert.That(store.GetId(a.RelatedNodes[0])).IsEqualTo(store.GetId(b));
		await Assert.That(b.RelatedNodes.Count).IsEqualTo(1);
		await Assert.That(store.GetId(b.RelatedNodes[0])).IsEqualTo(store.GetId(a));
	}

	[Test]
	public async Task Should_reject_the_reverse_direction_of_an_undirected_edge_as_a_structural_duplicate()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		store.GetCollection<RelatedTo>().Add(new RelatedTo { AObjectId = RefOf(store, a).ObjectId, BObjectId = RefOf(store, b).ObjectId });

		// B-A is the SAME structural key as A-B for an undirected edge — must be rejected.
		var ex = await Assert.ThrowsAsync(async () =>
		{
			store.GetCollection<RelatedTo>().Add(new RelatedTo { AObjectId = RefOf(store, b).ObjectId, BObjectId = RefOf(store, a).ObjectId });
		});
		await Assert.That(ex is InvalidOperationException).IsTrue();
	}
}
