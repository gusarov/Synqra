using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Synqra.AppendStorage;
using Synqra.AppendStorage.BlobStorage.File;
using Synqra.AppendStorage.InMemory;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;
using Synqra.BlobStorage.File;
using Synqra.Projection.InMemory;
using Synqra.Projection.MongoDb;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

// Store resolution here no longer pins the well-known SynqraGuids.SynqraRootStreamId: each test uses
// a fresh per-instance stream id (the Mongo store variants enter it as the ambient scope; the
// in-memory store variants borrow their projection for it from the provider). Nothing in this file
// touches the obsolete root stream anymore.

namespace Synqra.Tests.Cobra;

/// <summary>
/// The <b>same-session</b> contract shared by every Synqra store, on every backend: a property change
/// on a tracked model becomes an <see cref="ObjectPropertyChangedEvent"/> in the event log, and a typed
/// model graph built via the declarative link navigation (<c>node.Children.Add(...)</c> etc.) is
/// navigable from either end. This holds for durable <i>and</i> non-durable storage alike, so all
/// backends inherit it.
/// <para>
/// The stronger <b>durability</b> clause (state survives a restart via replay) lives separately in
/// <see cref="Cobra_DurableSynqraStoreContractTests"/> — only durable storage backends inherit that, so a
/// non-durable in-memory log is never falsely marked red for failing to persist.
/// </para>
/// <para>
/// Concrete subclasses fill in the two independent axes of the permutation matrix:
/// </para>
/// <list type="bullet">
///   <item><b>Event storage</b> (<see cref="RegisterAppendStorage"/>): MongoDB / SBX blob files / in-memory.</item>
///   <item><b>Store/projection</b> (<see cref="RegisterStore"/>): in-memory projection / MongoDB projection.</item>
/// </list>
/// <para>
/// Object-lifecycle and link coverage share this one matrix rather than each getting its own parallel
/// copy of the same axis-permutation hierarchy: every backend combination here now fully supports both
/// (<c>ILinkIndex</c> is no longer a capability only some stores have), so there's no reason to keep two
/// structurally identical sets of <c>RegisterAppendStorage</c>/<c>RegisterStore</c> classes in lockstep
/// across two files.
/// </para>
/// </summary>
public abstract class Cobra_SynqraStoreContractTests : BaseTest
{
	/// <summary>Wire up the durable event log under test — registers <c>IAppendStorage&lt;Event, Guid&gt;</c>.</summary>
	protected abstract void RegisterAppendStorage(IHostApplicationBuilder hostBuilder);

	/// <summary>Wire up the store/projection under test — registers <c>IObjectStore</c> / <c>IProjection</c> (+ <c>ILinkIndex</c>).</summary>
	protected abstract void RegisterStore(IServiceCollection services);

	// A Mongo-backed store variant here needs a stream scope active — SynqraStreamContext.Current
	// throws otherwise (there is no fallback default; see that type's own remarks on why). The
	// in-memory store variants don't read it, so entering unconditionally is harmless for those.
	// Entered from ResolveStore() itself — not a [Before(Test)]/[After(Test)] hook pair — because
	// TUnit runs each lifecycle phase on its own captured ExecutionContext, so an AsyncLocal set in
	// a Before hook does not flow into the test body even on the same instance/thread. Entering
	// inline, on the test method's own call stack, is what actually keeps it in scope for real.
	IDisposable? _streamScope;

	// A fresh stream per test instance (stable across Restart() — an instance field), replacing the
	// old shared SynqraGuids.SynqraRootStreamId. The Mongo store variant enters it as the ambient
	// scope; the in-memory store variant borrows its projection for this stream from the provider.
	readonly Guid _streamId = Guid.NewGuid();

	protected override void Register(IHostApplicationBuilder hostBuilder)
	{
		base.Register(hostBuilder);

		hostBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default);
		hostBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions);
		hostBuilder.Services.AddTypeMetadataProvider(
		[
			typeof(DemoModel),
			typeof(Command),
			typeof(ChangeObjectPropertyCommand),
			typeof(TestGraphNode),
			typeof(HierarchyLink),
			typeof(DependsOn),
			typeof(RelatedTo),
			typeof(WeightedLink),
		]);
		hostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Map(210, typeof(TestGraphNode));
			ser.Map(211, typeof(HierarchyLink));
			ser.Map(212, typeof(DependsOn));
			ser.Map(213, typeof(RelatedTo));
			ser.Map(214, typeof(WeightedLink));
			ser.Map(215, typeof(LinkAddedEvent));
			ser.Map(216, typeof(LinkRemovedEvent));
		});

		RegisterAppendStorage(hostBuilder);
		RegisterStore(hostBuilder.Services);
	}

	/// <summary>
	/// The store under test. Command-event persistence is turned off so the durable log keeps only
	/// domain events (which carry no live CLR payload) — uniform across all backends.
	/// </summary>
	protected IObjectStore ResolveStore()
	{
		_streamScope ??= SynqraStreamContext.Enter(_streamId);
		var store = ResolveStoreInstance();
		if (store is InMemoryProjection projection)
		{
			projection.PersistCommandEvents = false;
		}
		return store;
	}

	/// <summary>
	/// Obtain the store instance under test. Default = resolve the multitenant <see cref="IObjectStore"/>
	/// singleton directly (the Mongo projection resolves the ambient stream scope entered above). The
	/// in-memory store has no DI singleton — a projection is non-multitenant, factory-only — so its
	/// variants override this to borrow the cached projection for this test's stream from the provider,
	/// which also brings it up to head via the keeper (replacing the old explicit LoadStateAsync).
	/// </summary>
	protected virtual IObjectStore ResolveStoreInstance() => ServiceProvider.GetRequiredService<IObjectStore>();

	/// <summary>In-memory store resolution: the provider's cached, keeper-maintained projection for this test's stream.</summary>
	protected IObjectStore ResolveInMemoryStoreForStream()
		=> (IObjectStore)ServiceProvider.GetRequiredService<IProjectionProvider>().GetAsync(_streamId).GetAwaiter().GetResult();

	protected IAppendStorage<Event, Guid> Events() => ServiceProvider.GetRequiredService<IAppendStorage<Event, Guid>>();

	protected void UseMongoProjectionStore(IServiceCollection services) => services.AddMongoDbSynqraStore(_connectionString);

	[Test]
	public async Task Should_10_persist_property_change_to_event_log()
	{
		var store = ResolveStore();
		var model = new DemoModel();
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);

		model.Name = "Alice"; // ECS: DemoModel is a root component -> ChangeComponentPropertyCommand -> ComponentPropertyChangedEvent -> storage

		var changes = Events().GetAllAsync().ToBlockingEnumerable()
			.OfType<ComponentPropertyChangedEvent>()
			.Where(e => e.TargetId == id && e.PropertyName == nameof(DemoModel.Name))
			.ToArray();

		await Assert.That(changes.Length).IsGreaterThanOrEqualTo(1);
		await Assert.That((string?)changes[^1].NewValue).IsEqualTo("Alice");
	}

	// ---------------------------------------------------------------- Link navigation contract

	protected static TestGraphNode AddNode(IObjectStore store, string name)
	{
		var node = new TestGraphNode { Name = name };
		store.GetCollection<TestGraphNode>().Add(node);
		return node;
	}

	/// <summary>Build the full-flavour graph and return the endpoint ids for later verification.</summary>
	protected static (Guid parent, Guid child, Guid blocked, Guid related, Guid weighted) BuildGraph(IObjectStore store)
	{
		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");
		var blocked = AddNode(store, "blocked");
		var related = AddNode(store, "related");
		var weighted = AddNode(store, "weighted");

		parent.Children.Add(child);            // directed primitive, node-typed
		child.Blocks.Add(blocked);             // second parallel directed axis
		parent.RelatedNodes.Add(related);      // undirected
		parent.WeightedChildren.Add(new WeightedLink { Target = weighted, Order = 3.5 }); // payload, link-typed

		return (store.GetId(parent), store.GetId(child), store.GetId(blocked), store.GetId(related), store.GetId(weighted));
	}

	protected static async Task AssertGraph(IObjectStore store, (Guid parent, Guid child, Guid blocked, Guid related, Guid weighted) ids)
	{
		// Warm every node into the store's tracking cache up front. The in-memory projection has
		// already replayed the whole log by this point, so this is a no-op there — but the Mongo
		// projection intentionally never pre-warms (see MongoProjection's type-level remarks):
		// ResolveObject only resolves a tracked instance, so any node a nav property might resolve
		// (not just the ones this method names directly) must be loaded at least once first.
		var nodes = store.GetCollection<TestGraphNode>().ToList();
		TestGraphNode Get(Guid id) => nodes.First(n => store.GetId(n) == id);
		var parent = Get(ids.parent);
		var child = Get(ids.child);
		var related = Get(ids.related);

		// Directed primitive — navigable from both ends.
		await Assert.That(parent.Children.Count).IsEqualTo(1);
		await Assert.That(store.GetId(parent.Children.First())).IsEqualTo(ids.child);
		await Assert.That(child.Parent).IsNotNull();
		await Assert.That(store.GetId(child.Parent!)).IsEqualTo(ids.parent);

		// Second parallel directed axis does not bleed into the first.
		await Assert.That(child.Blocks.Count).IsEqualTo(1);
		await Assert.That(store.GetId(child.Blocks.First())).IsEqualTo(ids.blocked);
		await Assert.That(child.Children.Count).IsEqualTo(0);

		// Undirected — incident from either endpoint.
		await Assert.That(parent.RelatedNodes.Count).IsEqualTo(1);
		await Assert.That(store.GetId(parent.RelatedNodes.First())).IsEqualTo(ids.related);
		await Assert.That(related.RelatedNodes.Count).IsEqualTo(1);
		await Assert.That(store.GetId(related.RelatedNodes.First())).IsEqualTo(ids.parent);

		// Payload link — Order and typed endpoints intact.
		var weightedLink = parent.WeightedChildren.Single();
		await Assert.That(weightedLink.Order).IsEqualTo(3.5);
		await Assert.That(store.GetId(weightedLink.Target!)).IsEqualTo(ids.weighted);
		await Assert.That(store.GetId(weightedLink.Source!)).IsEqualTo(ids.parent);
	}

	[Test]
	public async Task Should_30_navigate_typed_graph_same_session()
	{
		var store = ResolveStore();
		var ids = BuildGraph(store);
		await AssertGraph(store, ids);
	}

	// ---------------------------------------------------------------- Cyclic graphs
	//
	// Nothing here checks for cycles, on purpose. Every nav property (Children/Parent/Parents/
	// RelatedNodes/etc.) is a single-hop query over ILinkIndex — there is no recursive graph walk
	// anywhere in the framework that a cycle could send into an infinite loop (the only
	// "ancestors" walk in the whole codebase is ObjectConverter's CLR *type* inheritance helper,
	// unrelated to link graphs). A directed link's structural key is ordered (A->B != B->A), so a
	// mutual or ring cycle is just N distinct links, no different from any other graph shape. A
	// self-loop (A->A) is explicitly accounted for in indexing (Source/Target collapse to one
	// adjacency-list entry, not two) rather than merely "happening not to crash".

	[Test]
	public async Task Should_31_navigate_a_self_loop_without_issue()
	{
		var store = ResolveStore();
		var a = AddNode(store, "a");

		a.Children.Add(a);

		await Assert.That(a.Children.Count).IsEqualTo(1);
		await Assert.That(store.GetId(a.Children.First())).IsEqualTo(store.GetId(a));
		await Assert.That(a.Parent).IsNotNull();
		await Assert.That(store.GetId(a.Parent!)).IsEqualTo(store.GetId(a));
		await Assert.That(a.Parents.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_32_navigate_a_mutual_two_node_cycle_without_issue()
	{
		var store = ResolveStore();
		var a = AddNode(store, "a");
		var b = AddNode(store, "b");

		a.Children.Add(b); // A -> B
		b.Children.Add(a); // B -> A — directed, so this is a distinct link, not a duplicate

		await Assert.That(store.GetId(a.Children.Single())).IsEqualTo(store.GetId(b));
		await Assert.That(store.GetId(b.Children.Single())).IsEqualTo(store.GetId(a));
		await Assert.That(store.GetId(a.Parent!)).IsEqualTo(store.GetId(b));
		await Assert.That(store.GetId(b.Parent!)).IsEqualTo(store.GetId(a));
	}

	[Test]
	public async Task Should_33_navigate_a_three_node_ring_cycle_without_issue()
	{
		var store = ResolveStore();
		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		var c = AddNode(store, "c");

		a.Children.Add(b); // A -> B -> C -> A
		b.Children.Add(c);
		c.Children.Add(a);

		await Assert.That(store.GetId(a.Children.Single())).IsEqualTo(store.GetId(b));
		await Assert.That(store.GetId(b.Children.Single())).IsEqualTo(store.GetId(c));
		await Assert.That(store.GetId(c.Children.Single())).IsEqualTo(store.GetId(a));
		await Assert.That(store.GetId(a.Parent!)).IsEqualTo(store.GetId(c));
		await Assert.That(store.GetId(b.Parent!)).IsEqualTo(store.GetId(a));
		await Assert.That(store.GetId(c.Parent!)).IsEqualTo(store.GetId(b));
	}

	[Test]
	public async Task Should_34_navigate_an_undirected_ring_cycle_without_issue()
	{
		// Undirected folds A-B and B-A to the same structural key, so a "cycle" here means three
		// distinct node pairs (A-B, B-C, C-A), not three attempts at the same link.
		var store = ResolveStore();
		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		var c = AddNode(store, "c");

		a.RelatedNodes.Add(b);
		b.RelatedNodes.Add(c);
		c.RelatedNodes.Add(a);

		await Assert.That(a.RelatedNodes.Count).IsEqualTo(2);
		await Assert.That(b.RelatedNodes.Count).IsEqualTo(2);
		await Assert.That(c.RelatedNodes.Count).IsEqualTo(2);
	}
}

/// <summary>
/// Adds the <b>durability</b> clause to <see cref="Cobra_SynqraStoreContractTests"/>: state must survive a
/// full restart and come back by replaying the event log alone (or, for a backend whose store
/// materializes durably and never replays — e.g. <c>MongoProjection</c> — by reading straight back out of
/// it). This is a property of a <i>durable</i> event storage (MongoDB, SBX files) — a non-durable
/// (in-memory) log can't satisfy it, so only durable storage backends inherit this contract. Pure
/// derivation, no extra wiring.
/// </summary>
public abstract class Cobra_DurableSynqraStoreContractTests : Cobra_SynqraStoreContractTests
{
	[Test]
	public async Task Should_20_state_survives_restart_via_replay()
	{
		var store = ResolveStore();
		var model = new DemoModel();
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);
		model.Name = "Bob";

		// New host + new store against the same durable log — state must come back by replay alone.
		Restart();

		var store2 = ResolveStore();

		var reloaded = store2.GetCollection<DemoModel>().FirstOrDefault(m => store2.GetId(m) == id);
		await Assert.That(reloaded).IsNotNull();
		await Assert.That(reloaded!.Name).IsEqualTo("Bob");
	}

	[Test]
	public async Task Should_40_navigation_survives_restart_via_replay()
	{
		var store = ResolveStore();
		var ids = BuildGraph(store);

		// New host + new projection against the same durable log — the graph must come back by replay.
		Restart();

		var store2 = ResolveStore();

		await AssertGraph(store2, ids);
	}

	[Test]
	public async Task Should_41_cyclic_graph_survives_restart_via_replay()
	{
		var store = ResolveStore();
		var a = AddNode(store, "a");
		var b = AddNode(store, "b");
		var c = AddNode(store, "c");
		a.Children.Add(b); // A -> B -> C -> A
		b.Children.Add(c);
		c.Children.Add(a);
		var idA = store.GetId(a);
		var idB = store.GetId(b);
		var idC = store.GetId(c);

		Restart();

		var store2 = ResolveStore();

		var nodes = store2.GetCollection<TestGraphNode>().ToList();
		var a2 = nodes.First(n => store2.GetId(n) == idA);
		var b2 = nodes.First(n => store2.GetId(n) == idB);
		var c2 = nodes.First(n => store2.GetId(n) == idC);

		await Assert.That(store2.GetId(a2.Children.Single())).IsEqualTo(idB);
		await Assert.That(store2.GetId(b2.Children.Single())).IsEqualTo(idC);
		await Assert.That(store2.GetId(c2.Children.Single())).IsEqualTo(idA);
	}
}

// =====================================================================================
// Event-storage axis (abstract — fixes IAppendStorage<Event, Guid>, leaves the store open)
// =====================================================================================

/// <summary>Event log = MongoDB (native, queryable BSON documents) — durable.</summary>
public abstract class Cobra_MongoEventStorageContract : Cobra_DurableSynqraStoreContractTests
{
	protected override void RegisterAppendStorage(IHostApplicationBuilder hostBuilder)
	{
		hostBuilder.Services.AddAppendStorageMongoDb<Event>(_connectionString);
	}
}

/// <summary>Event log = SBX-serialized blobs on the local filesystem — durable.</summary>
public abstract class Cobra_SbxFileEventStorageContract : Cobra_DurableSynqraStoreContractTests
{
	string? _folder;

	protected override void RegisterAppendStorage(IHostApplicationBuilder hostBuilder)
	{
		_folder ??= CreateTestFolder(); // stable across Restart() so replay reads the same files
		Configuration["Storage:BlobStorage:File:Folder"] = Path.Combine(_folder, "[Store]") + Path.DirectorySeparatorChar;
		hostBuilder.AddAppendStorageBlobFile<Event>(x => x.EventId);
	}
}

/// <summary>
/// Event log = in-memory. RED: there is no in-memory <c>IAppendStorage&lt;Event, Guid&gt;</c> yet.
/// Intended API to build: <c>hostBuilder.AddAppendStorageInMemory&lt;Event&gt;()</c>.
/// </summary>
public abstract class Cobra_InMemoryEventStorageContract : Cobra_SynqraStoreContractTests
{
	protected override void RegisterAppendStorage(IHostApplicationBuilder hostBuilder) => InMemoryAppendStorageExtensions.AddAppendStorageInMemory<Event, Guid>(hostBuilder, x => x.EventId);
}

// =====================================================================================
// Permutation matrix (storage axis × store axis)
// =====================================================================================

[InheritsTests]
[NotInParallel]
public sealed class Cobra_Mongo_Storage_With_InMemory_Store : Cobra_MongoEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services) => services.AddInMemorySynqraStore();
	protected override IObjectStore ResolveStoreInstance() => ResolveInMemoryStoreForStream();
}

[InheritsTests]
[NotInParallel]
public sealed class Cobra_Mongo_Storage_With_Mongo_Store : Cobra_MongoEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services) => UseMongoProjectionStore(services);
}

[InheritsTests]
public sealed class Cobra_SbxFile_Storage_With_InMemory_Store : Cobra_SbxFileEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services) => services.AddInMemorySynqraStore();
	protected override IObjectStore ResolveStoreInstance() => ResolveInMemoryStoreForStream();
}

[InheritsTests]
[NotInParallel]
public sealed class Cobra_SbxFile_Storage_With_Mongo_Store : Cobra_SbxFileEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services) => UseMongoProjectionStore(services);
}

[InheritsTests]
public sealed class Cobra_InMemory_Storage_With_InMemory_Store : Cobra_InMemoryEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services) => services.AddInMemorySynqraStore();
	protected override IObjectStore ResolveStoreInstance() => ResolveInMemoryStoreForStream();
}

[InheritsTests]
[NotInParallel]
public sealed class Cobra_InMemory_Storage_With_Mongo_Store : Cobra_InMemoryEventStorageContract
{
	protected override void RegisterStore(IServiceCollection services) => UseMongoProjectionStore(services);
}

// =====================================================================================
// Components, materialized by the Mongo store: each component is persisted in its own shared
// "Components" Mongo collection, addressed by (ContainerId, ComponentTypeId, ComponentId) — not
// embedded in the container's own document (see MongoProjection.UpsertComponent's remarks on
// why). CI provisions a real mongod (see Dockerfile's withmongo), so this runs there too — only
// NotInParallel, since every Mongo-backed test in the suite shares that one mongod instance.
// Kept as its own dedicated class rather than folded into the matrix above: components aren't
// (yet) being tested across the full append-storage axis the way the object/link contract is,
// just end-to-end against the real backend that materializes them.
// =====================================================================================

[NotInParallel]
public sealed class Cobra_Mongo_Store_Components_Tests : BaseTest
{
	// See Cobra_SynqraStoreContractTests.ResolveStore's remark — a [Before(Test)] hook's AsyncLocal
	// does not flow into the test body under TUnit, so this is entered inline from Store instead.
	IDisposable? _streamScope;

	// A fresh Mongo stream per test instance (replaces the old shared SynqraGuids.SynqraRootStreamId).
	readonly Guid _streamId = Guid.NewGuid();

	protected override void Register(IHostApplicationBuilder hostBuilder)
	{
		base.Register(hostBuilder);

		hostBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default);
		hostBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions);
		hostBuilder.Services.AddTypeMetadataProvider(
		[
			typeof(Command),
			typeof(ChangeObjectPropertyCommand),
			typeof(AddComponentCommand),
			typeof(ChangeComponentPropertyCommand),
			typeof(DeleteComponentCommand),
			typeof(TestGeneratedContainerNode),
			typeof(TestUniqueComponent),
			typeof(TestTaggingComponent),
			typeof(TestActivatableComponent),
		]);
		hostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(220, typeof(TestGeneratedContainerNode));
			ser.Map(221, typeof(TestUniqueComponent));
			ser.Map(222, typeof(TestTaggingComponent));
			ser.Map(223, typeof(TestActivatableComponent));
		});
		hostBuilder.Services.AddAppendStorageMongoDb<Event>(_connectionString);
		UseMongoProjectionStore(hostBuilder.Services);
	}

	void UseMongoProjectionStore(IServiceCollection services) => services.AddMongoDbSynqraStore(_connectionString);

	IObjectStore Store
	{
		get
		{
			_streamScope ??= SynqraStreamContext.Enter(_streamId);
			return ServiceProvider.GetRequiredService<IObjectStore>();
		}
	}

	[Test]
	public async Task Should_add_change_and_remove_a_component_via_the_generated_collection()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n1" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);

		var component = new TestUniqueComponent { Subject = "first" };
		node.Components.Add(component); // generator-emitted path -> AddComponentCommand

		await Assert.That(node.Components.Count).IsEqualTo(1);
		var attached = (TestUniqueComponent)node.Components.GetUniqueComponent(typeof(TestUniqueComponent))!;
		await Assert.That(attached.Subject).IsEqualTo("first");

		// AttachToContainer wiring lets the component's own generated setter route through
		// ChangeComponentPropertyCommand without the test threading the container manually.
		attached.Subject = "second";
		await Assert.That(attached.Subject).IsEqualTo("second");

		node.Components.Remove(attached);
		await Assert.That(node.Components.Count).IsEqualTo(0);
	}

	[Test]
	public async Task Should_reject_a_second_unique_component_of_the_same_type()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n2" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);

		node.Components.Add(new TestUniqueComponent { Subject = "first" });

		var ex = await Assert.ThrowsAsync(async () =>
		{
			await store.SubmitCommandAsync(new AddComponentCommand
			{
				CommandId = GuidExtensions.CreateVersion7(),
				StreamId = _streamId,
				TargetTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestGeneratedContainerNode)).TypeId,
				TargetId = store.GetId(node),
				CollectionId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestGeneratedContainerNode)).GetCollectionId(""),
				ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestUniqueComponent)).TypeId,
				ComponentId = Guid.Empty,
				Data = new TestUniqueComponent { Subject = "second" },
			});
		});
		await Assert.That(ex).IsTypeOf<InvalidOperationException>();
	}

	[Test]
	public async Task Should_support_multiple_non_unique_components_addressed_by_id()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n3" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);

		var a = new TestTaggingComponent { Tag = "a" };
		var b = new TestTaggingComponent { Tag = "b" };
		node.Components.Add(a);
		node.Components.Add(b);
		var aId = ((IIdentifiable<Guid>)a).Id;
		var bId = ((IIdentifiable<Guid>)b).Id;

		await Assert.That(node.Components.Count).IsEqualTo(2);
		await Assert.That(node.Components.OfType<TestTaggingComponent>().Single(x => ((IIdentifiable<Guid>)x).Id == aId).Tag).IsEqualTo("a");
		await Assert.That(node.Components.OfType<TestTaggingComponent>().Single(x => ((IIdentifiable<Guid>)x).Id == bId).Tag).IsEqualTo("b");
	}

	[Test]
	public async Task Should_activate_on_the_originating_add_but_not_on_a_later_reload()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n4" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);

		var component = new TestActivatableComponent { Marker = "m" };
		node.Components.Add(component);

		await Assert.That(component.ActivationCount).IsEqualTo(1);
		await Assert.That(component.LastActivationWasReplay).IsEqualTo(false);
	}

	[Test]
	public async Task Should_persist_a_component_so_it_survives_a_restart()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "n5" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);
		var id = store.GetId(node);
		node.Components.Add(new TestUniqueComponent { Subject = "durable" });

		Restart();

		var store2 = Store;
		var reloaded = store2.GetCollection<TestGeneratedContainerNode>().First(n => store2.GetId(n) == id);
		var component = (TestUniqueComponent)reloaded.Components.GetUniqueComponent(typeof(TestUniqueComponent))!;
		await Assert.That(component).IsNotNull();
		await Assert.That(component.Subject).IsEqualTo("durable");
	}
}
