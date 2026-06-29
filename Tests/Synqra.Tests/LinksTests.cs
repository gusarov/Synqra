using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Synqra.BinarySerializer;
using Synqra.Projection.InMemory;

namespace Synqra.Tests;

// ---- Sample link/node models exercising every flavour the Todo solution's
// IHierarchy/IMHierarchy/IMRHierarchy/IDependable family hand-rolled separately
// (see plans/links.md). One framework primitive (Link/DirectedLink/UndirectedLink),
// differentiated by concrete type, replaces all four — and the consumer never sees a
// raw Guid or an imperative Link() call: a typed model with [To]/[From]/[Related]
// navigation properties is the whole surface, and mutating a collection IS the link.
// A link is an attached fact (AddLinkCommand/LinkAddedEvent), not a top-level object —
// it never rides CreateObjectCommand/ObjectCreatedEvent the way a plain object does.

/// <summary>Primitive parent→child link (no payload). Same type serves "single parent"
/// (IHierarchy) and "multi parent" (IMHierarchy) — the difference is purely how many a
/// caller creates.</summary>
[SynqraModel]
[Schema(2026.510, "1 LinkId Guid SourceId Guid TargetId Guid")]
public partial class HierarchyLink : DirectedLink<TestGraphNode, TestGraphNode>
{
}

/// <summary>Pure marker link (IDependable's "blocks/blockedBy"). Primitive — adds no fields of
/// its own, but still needs [SynqraModel]/[Schema] (unlike the framework's own plain marker
/// subclasses, e.g. ObjectDeletedEvent): SbxSerializer.Map requires schema info on the specific
/// type being mapped, not just on an inherited base.</summary>
[SynqraModel]
[Schema(2026.518, "1 LinkId Guid SourceId Guid TargetId Guid")]
public partial class DependsOn : DirectedLink<TestGraphNode, TestGraphNode>
{
}

/// <summary>Undirected relation — absent from the Todo predecessor, new in this design. Primitive.</summary>
[SynqraModel]
[Schema(2026.511, "1 LinkId Guid SourceId Guid TargetId Guid")]
public partial class RelatedTo : UndirectedLink<TestGraphNode, TestGraphNode>
{
}

/// <summary>Directed link that carries its own data (IMRHierarchy's "the relation has payload").
/// Because it is a payload link, it is navigated link-typed — the consumer works with the link,
/// not the node — so the payload stays reachable.</summary>
[SynqraModel]
[Schema(2026.512, "1 LinkId Guid SourceId Guid TargetId Guid Order double?")]
public partial class WeightedLink : DirectedLink<TestGraphNode, TestGraphNode>
{
	public partial double? Order { get; set; }
}

[SynqraModel]
[Schema(2026.513, "1 Name string?")]
public partial class TestGraphNode
{
	public partial string? Name { get; set; }

	// Hierarchy (directed, primitive): node-typed both ways — the link is invisible.
	[To(typeof(HierarchyLink))]
	public partial ICollection<TestGraphNode> Children { get; }

	[From(typeof(HierarchyLink))]
	public partial IReadOnlyList<TestGraphNode> Parents { get; }

	// Opt-in setter: declaring { get; set; } (not { get; }) is what turns this on — see
	// LinkNavigationAttributes.ToAttribute remarks. child.Parent = newParent replaces whatever
	// single parent link already exists with a new one (or clears it on null).
	[From(typeof(HierarchyLink))]
	public partial TestGraphNode? Parent { get; set; }

	// Dependency (directed, primitive): a second, parallel axis between the same nodes.
	[To(typeof(DependsOn))]
	public partial ICollection<TestGraphNode> Blocks { get; }

	[From(typeof(DependsOn))]
	public partial IReadOnlyList<TestGraphNode> BlockedBy { get; }

	// Undirected (primitive): incident from either side.
	[Related(typeof(RelatedTo))]
	public partial ICollection<TestGraphNode> RelatedNodes { get; }

	// Payload link: navigated link-typed so Order is reachable.
	[To]
	public partial ICollection<WeightedLink> WeightedChildren { get; }

	[From]
	public partial IReadOnlyList<WeightedLink> WeightedParentLinks { get; }
}

// ---- Parametrized links between DIFFERENT node types ----
// Verifies the generic Link<TSource, TTarget> keeps the two endpoint types distinct: a
// node-typed nav resolves the opposite end to the *other* concrete type, and typed Source/Target
// stay strongly typed across the boundary.

/// <summary>Primitive directed link between two different node types (Document → Folder).</summary>
public sealed class FiledIn : DirectedLink<TestDocNode, TestFolderNode>
{
}

/// <summary>Payload directed link between two different node types (Document → Tag), navigated
/// link-typed so the payload stays reachable.</summary>
[SynqraModel]
[Schema(2026.514, "1 LinkId Guid SourceId Guid TargetId Guid AddedBy string?")]
public partial class TaggedWith : DirectedLink<TestDocNode, TestTagNode>
{
	public partial string? AddedBy { get; set; }
}

[SynqraModel]
[Schema(2026.515, "1 Title string?")]
public partial class TestDocNode
{
	public partial string? Title { get; set; }

	// Node-typed nav to a *different* node type via a primitive link.
	[To(typeof(FiledIn))]
	public partial ICollection<TestFolderNode> Folders { get; }

	// Link-typed nav to a payload link whose Target is yet another node type.
	[To]
	public partial ICollection<TaggedWith> Tags { get; }
}

[SynqraModel]
[Schema(2026.516, "1 Name string?")]
public partial class TestFolderNode
{
	public partial string? Name { get; set; }

	[From(typeof(FiledIn))]
	public partial IReadOnlyList<TestDocNode> Documents { get; }
}

[SynqraModel]
[Schema(2026.517, "1 Label string?")]
public partial class TestTagNode
{
	public partial string? Label { get; set; }

	[From]
	public partial IReadOnlyList<TaggedWith> TaggedDocuments { get; }
}

public class LinksTests
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
			typeof(HierarchyLink),
			typeof(DependsOn),
			typeof(RelatedTo),
			typeof(WeightedLink),
			typeof(TestDocNode),
			typeof(TestFolderNode),
			typeof(TestTagNode),
			typeof(FiledIn),
			typeof(TaggedWith)
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

	// ---- Flavour: IHierarchy (single parent) ----

	[Test]
	public async Task Should_navigate_single_parent_and_children()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");

		// Mutating the collection IS the link — no Guid, no store.Link.
		parent.Children.Add(child);

		await Assert.That(child.Parent).IsNotNull();
		await Assert.That(store.GetId(child.Parent!)).IsEqualTo(store.GetId(parent));
		await Assert.That(parent.Children.Count).IsEqualTo(1);
		await Assert.That(store.GetId(parent.Children.First())).IsEqualTo(store.GetId(child));
	}

	// ---- Flavour: IMHierarchy (multi-parent — same link type, no constraint) ----

	[Test]
	public async Task Should_support_multiple_parents_via_the_same_link_type()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parentA = AddNode(store, "parentA");
		var parentB = AddNode(store, "parentB");
		var child = AddNode(store, "child");

		parentA.Children.Add(child);
		parentB.Children.Add(child);

		await Assert.That(child.Parents.Count).IsEqualTo(2);
		// Parent degrades to "first" once there's more than one — multi-parent callers use Parents.
		await Assert.That(child.Parent).IsNotNull();
	}

	// ---- Flavour: IMRHierarchy (the relation carries its own data) ----

	[Test]
	public async Task Should_carry_payload_on_the_link_itself()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");

		// Link-typed navigation: the consumer sets the other end + payload, the collection
		// stamps the owner's end.
		parent.WeightedChildren.Add(new WeightedLink { Target = child, Order = 2.5 });

		var stored = parent.WeightedChildren.Single();
		await Assert.That(stored.Order).IsEqualTo(2.5);
		await Assert.That(store.GetId(stored.Target!)).IsEqualTo(store.GetId(child));
		await Assert.That(store.GetId(stored.Source!)).IsEqualTo(store.GetId(parent));
		await Assert.That(child.WeightedParentLinks.Count).IsEqualTo(1);
	}

	// ---- Flavour: IDependable (a different link type between the same endpoints coexists) ----

	[Test]
	public async Task Should_differentiate_coexisting_link_types_between_the_same_endpoints()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();
		var index = (ILinkIndex)store;

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");

		a.Children.Add(b);
		a.Blocks.Add(b);

		var between = index.LinksBetween(store.GetId(a), store.GetId(b), typeof(Link));
		await Assert.That(between.Count).IsEqualTo(2);
		await Assert.That(between.OfType<HierarchyLink>().Count()).IsEqualTo(1);
		await Assert.That(between.OfType<DependsOn>().Count()).IsEqualTo(1);

		await Assert.That(b.BlockedBy.Count).IsEqualTo(1);
		await Assert.That(a.Blocks.Count).IsEqualTo(1);
		// The two axes do not bleed into each other.
		await Assert.That(a.Children.Count).IsEqualTo(1);
		await Assert.That(b.Parents.Count).IsEqualTo(1);
	}

	// ---- Structural dedup: adding the same link twice is idempotent ----

	[Test]
	public async Task Should_treat_re_adding_an_existing_link_as_a_no_op()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");

		a.Children.Add(b);
		a.Children.Add(b); // same structural key — no second link

		await Assert.That(a.Children.Count).IsEqualTo(1);
	}

	// ---- Structural dedup at the store level (AddLinkCommand submitted directly vetoes duplicates) ----

	[Test]
	public async Task Should_reject_a_structurally_duplicate_directed_link_submitted_directly()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		a.Children.Add(b); // first HierarchyLink a -> b, via the normal nav path

		// Same structural key (HierarchyLink, a -> b), submitted directly instead of via Children.Add —
		// must be rejected exactly like the nav-collection path would (it just doesn't swallow it as a no-op).
		var ex = await Assert.ThrowsAsync(async () =>
		{
			await store.SubmitCommandAsync(new AddLinkCommand
			{
				CommandId = GuidExtensions.CreateVersion7(),
				LinkTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(HierarchyLink)).TypeId,
				LinkId = GuidExtensions.CreateVersion7(),
				SourceId = store.GetId(a),
				TargetId = store.GetId(b),
				Data = new HierarchyLink { SourceId = store.GetId(a), TargetId = store.GetId(b) },
			});
		});
		await Assert.That(ex is InvalidOperationException).IsTrue();
	}

	[Test]
	public async Task Should_allow_the_reverse_direction_of_a_directed_link_as_distinct()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");

		a.Children.Add(b);
		b.Children.Add(a); // B -> A is a different structural key for a directed link

		await Assert.That(a.Children.Count).IsEqualTo(1);
		await Assert.That(b.Children.Count).IsEqualTo(1);
	}

	// ---- Undirected: order-independent structural key ----

	[Test]
	public async Task Should_treat_undirected_link_as_incident_from_either_endpoint()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");

		a.RelatedNodes.Add(b);

		await Assert.That(a.RelatedNodes.Count).IsEqualTo(1);
		await Assert.That(store.GetId(a.RelatedNodes.First())).IsEqualTo(store.GetId(b));
		await Assert.That(b.RelatedNodes.Count).IsEqualTo(1);
		await Assert.That(store.GetId(b.RelatedNodes.First())).IsEqualTo(store.GetId(a));
	}

	[Test]
	public async Task Should_reject_the_reverse_direction_of_an_undirected_link_as_a_structural_duplicate()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		a.RelatedNodes.Add(b);

		// B-A is the SAME structural key as A-B for an undirected link — must stay a no-op, not a second link.
		b.RelatedNodes.Add(a);

		await Assert.That(a.RelatedNodes.Count).IsEqualTo(1);
	}

	// ---- Parametrized links: endpoints are two different node types ----

	[Test]
	public async Task Should_resolve_typed_endpoints_across_different_node_types()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var doc = new TestDocNode { Title = "spec" };
		var folder = new TestFolderNode { Name = "inbox" };
		store.GetCollection<TestDocNode>().Add(doc);
		store.GetCollection<TestFolderNode>().Add(folder);

		doc.Folders.Add(folder);

		await Assert.That(doc.Folders.Count).IsEqualTo(1);
		// The forward nav yields the Folder type; the reverse nav yields the Document type.
		TestFolderNode resolvedFolder = doc.Folders.First();
		TestDocNode resolvedDoc = folder.Documents.First();
		await Assert.That(store.GetId(resolvedFolder)).IsEqualTo(store.GetId(folder));
		await Assert.That(folder.Documents.Count).IsEqualTo(1);
		await Assert.That(store.GetId(resolvedDoc)).IsEqualTo(store.GetId(doc));
	}

	[Test]
	public async Task Should_carry_payload_on_a_cross_type_link()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var doc = new TestDocNode { Title = "spec" };
		var tag = new TestTagNode { Label = "urgent" };
		store.GetCollection<TestDocNode>().Add(doc);
		store.GetCollection<TestTagNode>().Add(tag);

		doc.Tags.Add(new TaggedWith { Target = tag, AddedBy = "me" });

		var link = doc.Tags.Single();
		await Assert.That(link.AddedBy).IsEqualTo("me");
		// Typed Source/Target resolve to the two distinct concrete node types.
		TestDocNode src = link.Source!;
		TestTagNode dst = link.Target!;
		await Assert.That(store.GetId(src)).IsEqualTo(store.GetId(doc));
		await Assert.That(store.GetId(dst)).IsEqualTo(store.GetId(tag));
		await Assert.That(tag.TaggedDocuments.Count).IsEqualTo(1);
		await Assert.That(store.GetId(tag.TaggedDocuments.Single().Source!)).IsEqualTo(store.GetId(doc));
	}

	// ---- Removal: Remove/Clear now work (they used to throw NotSupportedException) ----

	[Test]
	public async Task Should_remove_a_link_via_the_collection()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");
		parent.Children.Add(child);
		await Assert.That(parent.Children.Count).IsEqualTo(1);

		var removed = parent.Children.Remove(child);

		await Assert.That(removed).IsTrue();
		await Assert.That(parent.Children.Count).IsEqualTo(0);
		await Assert.That(child.Parent).IsNull();
	}

	[Test]
	public async Task Should_clear_all_links_of_a_type_via_the_collection()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var childA = AddNode(store, "childA");
		var childB = AddNode(store, "childB");
		parent.Children.Add(childA);
		parent.Children.Add(childB);
		await Assert.That(parent.Children.Count).IsEqualTo(2);

		parent.Children.Clear();

		await Assert.That(parent.Children.Count).IsEqualTo(0);
		await Assert.That(childA.Parent).IsNull();
		await Assert.That(childB.Parent).IsNull();
	}

	// ---- Opt-in setter: child.Parent = newParent (the symmetric counterpart to parent.Children.Add) ----

	[Test]
	public async Task Should_assign_parent_via_the_opt_in_setter()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");

		child.Parent = parent;

		await Assert.That(child.Parent).IsNotNull();
		await Assert.That(store.GetId(child.Parent!)).IsEqualTo(store.GetId(parent));
		await Assert.That(parent.Children.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_replace_the_existing_parent_when_the_setter_is_assigned_again()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var oldParent = AddNode(store, "oldParent");
		var newParent = AddNode(store, "newParent");
		var child = AddNode(store, "child");

		child.Parent = oldParent;
		child.Parent = newParent;

		await Assert.That(store.GetId(child.Parent!)).IsEqualTo(store.GetId(newParent));
		await Assert.That(oldParent.Children.Count).IsEqualTo(0);
		await Assert.That(newParent.Children.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_clear_the_parent_when_the_setter_is_assigned_null()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");
		child.Parent = parent;

		child.Parent = null;

		await Assert.That(child.Parent).IsNull();
		await Assert.That(parent.Children.Count).IsEqualTo(0);
	}

	// ---- INotifyPropertyChanged: nav properties have no backing field, so the store has to tell
	// both endpoints explicitly that a relevant link changed (see ILinkAware) — this is the part
	// that does NOT just fall out of "Children/Parent are live queries over the same index". ----

	[Test]
	public async Task Should_raise_PropertyChanged_for_Children_and_Parent_on_both_endpoints_when_a_link_is_added()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");

		var parentChanged = new List<string>();
		var childChanged = new List<string>();
		((INotifyPropertyChanged)parent).PropertyChanged += (_, e) => parentChanged.Add(e.PropertyName!);
		((INotifyPropertyChanged)child).PropertyChanged += (_, e) => childChanged.Add(e.PropertyName!);

		parent.Children.Add(child);

		await Assert.That(parentChanged).Contains(nameof(TestGraphNode.Children));
		await Assert.That(childChanged).Contains(nameof(TestGraphNode.Parent));
		await Assert.That(childChanged).Contains(nameof(TestGraphNode.Parents));
	}

	[Test]
	public async Task Should_raise_PropertyChanged_on_both_endpoints_when_a_link_is_removed()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");
		parent.Children.Add(child);

		var parentChanged = new List<string>();
		var childChanged = new List<string>();
		((INotifyPropertyChanged)parent).PropertyChanged += (_, e) => parentChanged.Add(e.PropertyName!);
		((INotifyPropertyChanged)child).PropertyChanged += (_, e) => childChanged.Add(e.PropertyName!);

		parent.Children.Remove(child);

		await Assert.That(parentChanged).Contains(nameof(TestGraphNode.Children));
		await Assert.That(childChanged).Contains(nameof(TestGraphNode.Parent));
	}

	[Test]
	public async Task Should_not_raise_PropertyChanged_for_an_unrelated_nav_property_on_a_different_link_type()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");

		var aChanged = new List<string>();
		((INotifyPropertyChanged)a).PropertyChanged += (_, e) => aChanged.Add(e.PropertyName!);

		a.Blocks.Add(b); // DependsOn, not HierarchyLink

		await Assert.That(aChanged).Contains(nameof(TestGraphNode.Blocks));
		await Assert.That(aChanged).DoesNotContain(nameof(TestGraphNode.Children));
	}

	[Test]
	public async Task Should_raise_PropertyChanged_for_RelatedNodes_on_both_endpoints_of_an_undirected_link()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");

		var aChanged = new List<string>();
		var bChanged = new List<string>();
		((INotifyPropertyChanged)a).PropertyChanged += (_, e) => aChanged.Add(e.PropertyName!);
		((INotifyPropertyChanged)b).PropertyChanged += (_, e) => bChanged.Add(e.PropertyName!);

		a.RelatedNodes.Add(b);

		await Assert.That(aChanged).Contains(nameof(TestGraphNode.RelatedNodes));
		await Assert.That(bChanged).Contains(nameof(TestGraphNode.RelatedNodes));
	}

	// ---- GetCollection<TLink>() footgun: links never ride the generic object collection, so
	// calling GetCollection<T>() for a Link-derived type used to silently come back empty instead
	// of surfacing the mistake — must throw a clear error pointing at ILinkIndex instead. ----

	[Test]
	public async Task Should_throw_a_clear_error_when_GetCollection_is_called_for_a_link_type()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		a.Children.Add(b); // a real HierarchyLink now exists — proves the throw isn't masking an empty-anyway case

		var ex = await Assert.ThrowsAsync(async () =>
		{
			store.GetCollection<HierarchyLink>();
			await Task.CompletedTask;
		});

		await Assert.That(ex is NotSupportedException).IsTrue();
		await Assert.That(ex!.Message).Contains("ILinkIndex");
	}

	[Test]
	public async Task Should_throw_a_clear_error_when_the_non_generic_GetCollection_is_called_for_a_link_type()
	{
		var sp = BuildServices();
		var store = sp.GetRequiredService<IObjectStore>();

		var ex = await Assert.ThrowsAsync(async () =>
		{
			store.GetCollection(typeof(HierarchyLink), null);
			await Task.CompletedTask;
		});

		await Assert.That(ex is NotSupportedException).IsTrue();
		await Assert.That(ex!.Message).Contains("ILinkIndex");
	}
}
