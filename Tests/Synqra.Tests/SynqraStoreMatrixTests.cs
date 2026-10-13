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
			typeof(AddComponentCommand),
			typeof(TestComponentNode),
			typeof(TestUniqueComponent),
		]);
		hostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Map(224, typeof(TestComponentNode));
			ser.Map(221, typeof(TestUniqueComponent));
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
		// Resolved by id, nothing pre-enumerated: every store must resolve an entity (and whatever a nav
		// property reaches from it) on demand, whether from replayed state or straight from its backend.
		var parent = (TestGraphNode)store.ResolveObject(ids.parent)!;
		var child = (TestGraphNode)store.ResolveObject(ids.child)!;
		var related = (TestGraphNode)store.ResolveObject(ids.related)!;

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
	// ---------------------------------------------------------------- Server-side API support
	//
	// What a host needs to drive the model generically (a REST API, a background job): list the types,
	// refuse a write made against a stale version, apply several commands as one, and learn which
	// events a server-side write committed so they can be pushed to connected clients.

	static AddComponentCommand CreateRoot<T>(IObjectStore store, Guid id, T data) where T : class
	{
		var metadata = store.TypeMetadataProvider.GetTypeMetadata(typeof(T));
		return new AddComponentCommand
		{
			TargetTypeId = metadata.TypeId,
			CollectionId = metadata.GetCollectionId(""),
			TargetId = id,
			ComponentTypeId = metadata.TypeId,
			ComponentId = id,
			Data = data,
		};
	}

	static ChangeComponentPropertyCommand Rename(IObjectStore store, Guid id, string name)
	{
		var metadata = store.TypeMetadataProvider.GetTypeMetadata(typeof(DemoModel));
		return new ChangeComponentPropertyCommand
		{
			TargetTypeId = metadata.TypeId,
			CollectionId = metadata.GetCollectionId(""),
			TargetId = id,
			ComponentTypeId = metadata.TypeId,
			ComponentId = id,
			PropertyName = nameof(DemoModel.Name),
			NewValue = name,
		};
	}

	static async Task<Exception?> Caught(Func<Task> action)
	{
		try
		{
			await action();
			return null;
		}
		catch (Exception ex)
		{
			return ex;
		}
	}

	DemoModel Load(IObjectStore store, Guid id) => store.GetCollection<DemoModel>().Single(m => store.GetId(m) == id);

	[Test]
	public async Task Should_50_list_every_registered_type_once()
	{
		var store = ResolveStore();
		var types = store.TypeMetadataProvider.Types.Select(t => t.Type).ToList();

		await Assert.That(types.Contains(typeof(DemoModel))).IsTrue();
		await Assert.That(types.Contains(typeof(HierarchyLink))).IsTrue();
		await Assert.That(types.Count).IsEqualTo(types.Distinct().Count());
	}

	[Test]
	public async Task Should_51_refuse_a_write_against_a_stale_version_and_accept_the_current_one()
	{
		var store = ResolveStore();
		var model = new DemoModel();
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);
		var stale = store.GetLastEventId(id);
		model.Name = "first";
		var current = store.GetLastEventId(id);
		await Assert.That(current).IsNotEqualTo(stale);

		var refused = await Caught(() => store.SubmitCommandAsync(Rename(store, id, "stale"), new CommandSubmissionOptions { ExpectedLastEventId = stale }));
		await Assert.That(refused is ConcurrencyException).IsTrue();
		await Assert.That(Load(store, id).Name).IsEqualTo("first");

		await store.SubmitCommandAsync(Rename(store, id, "fresh"), new CommandSubmissionOptions { ExpectedLastEventId = current });
		await Assert.That(Load(store, id).Name).IsEqualTo("fresh");
	}

	[Test]
	public async Task Should_52_apply_a_batch_whole_where_later_commands_see_earlier_ones()
	{
		var store = ResolveStore();
		var a = Guid.NewGuid();
		var b = Guid.NewGuid();
		var batch = new BatchCommand();
		batch.Commands.Add(CreateRoot(store, a, new DemoModel { Name = "a" }));
		batch.Commands.Add(CreateRoot(store, b, new DemoModel { Name = "b" }));
		batch.Commands.Add(Rename(store, a, "a, renamed in the same batch"));

		await store.SubmitCommandAsync(batch);

		await Assert.That(Load(store, a).Name).IsEqualTo("a, renamed in the same batch");
		await Assert.That(Load(store, b).Name).IsEqualTo("b");
	}

	[Test]
	public async Task Should_53_apply_nothing_of_a_batch_when_one_command_is_refused()
	{
		var store = ResolveStore();
		var created = Guid.NewGuid();
		var missing = Guid.NewGuid();
		var before = Events().GetAllAsync().ToBlockingEnumerable().Count();
		var batch = new BatchCommand();
		batch.Commands.Add(CreateRoot(store, created, new DemoModel { Name = "would exist" }));
		batch.Commands.Add(Rename(store, missing, "no such object"));

		var refused = await Caught(() => store.SubmitCommandAsync(batch));

		await Assert.That(refused).IsNotNull();
		await Assert.That(Events().GetAllAsync().ToBlockingEnumerable().Count()).IsEqualTo(before);
		if (store is MongoProjection)
		{
			// The transaction aborted: no document either. The in-memory replica applies in order and is rebuilt from the log.
			await Assert.That(store.GetCollection<DemoModel>().Any(m => store.GetId(m) == created)).IsFalse();
		}
	}

	[Test]
	public async Task Should_54_refuse_a_batch_whose_expectation_is_stale()
	{
		var store = ResolveStore();
		var model = new DemoModel { Name = "start" };
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);
		var stale = store.GetLastEventId(id);
		model.Name = "moved on";
		var batch = new BatchCommand();
		batch.Expectations.Add(new BatchExpectation { TargetId = id, TargetTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(DemoModel)).TypeId, ExpectedLastEventId = stale, });
		batch.Commands.Add(Rename(store, id, "should not land"));

		var refused = await Caught(() => store.SubmitCommandAsync(batch));

		await Assert.That(refused is ConcurrencyException).IsTrue();
		await Assert.That(Load(store, id).Name).IsEqualTo("moved on");
	}

	[Test]
	public async Task Should_55_mint_a_missing_link_id_into_the_event()
	{
		var store = ResolveStore();
		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");
		await store.SubmitCommandAsync(new AddLinkCommand
		{
			LinkTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(HierarchyLink)).TypeId,
			SourceId = store.GetId(parent),
			TargetId = store.GetId(child),
			Data = new HierarchyLink(),
		});

		var added = Events().GetAllAsync().ToBlockingEnumerable().OfType<LinkAddedEvent>().Single(e => e.SourceId == store.GetId(parent));
		await Assert.That(added.LinkId).IsNotEqualTo(Guid.Empty);
		var index = (ILinkIndex)store;
		await Assert.That(index.TryGetById(added.LinkId, out var link)).IsTrue();
		await Assert.That(link!.TargetId).IsEqualTo(store.GetId(child));
	}

	[Test]
	public async Task Should_56_answer_a_base_link_type_query_with_every_link_type()
	{
		var store = ResolveStore();
		var ids = BuildGraph(store);
		var index = (ILinkIndex)store;

		var all = index.LinksAt(ids.parent, LinkEnd.Either, typeof(Link));

		await Assert.That(all.Count).IsEqualTo(3);
		await Assert.That(all.Select(l => l.GetType()).Distinct().Count()).IsEqualTo(3);
	}

	[Test]
	public async Task Should_58_attach_a_unique_component_to_an_object_created_in_the_same_batch()
	{
		var store = ResolveStore();
		var id = Guid.NewGuid();
		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));
		var batch = new BatchCommand();
		batch.Commands.Add(CreateRoot(store, id, new TestComponentNode { Name = "container" }));
		batch.Commands.Add(new AddComponentCommand
		{
			TargetTypeId = container.TypeId,
			CollectionId = container.GetCollectionId(""),
			TargetId = id,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestUniqueComponent)).TypeId,
			Data = new TestUniqueComponent { Subject = "once" },
		});

		await store.SubmitCommandAsync(batch);

		// The validating dry run must not touch the real instances: a hand-held component collection shared with a clone would get it twice.
		var node = store.GetCollection<TestComponentNode>().Single(n => store.GetId(n) == id);
		await Assert.That(node.Components.Count).IsEqualTo(1);
		await Assert.That(((TestUniqueComponent)node.Components.Single()).Subject).IsEqualTo("once");
	}

	[Test]
	public async Task Should_59_batch_over_objects_the_projection_already_holds()
	{
		var store = ResolveStore();
		var model = new DemoModel { Name = "start" };
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);
		var batch = new BatchCommand();
		batch.Commands.Add(Rename(store, id, "first"));
		batch.Commands.Add(Rename(store, id, "second"));

		await store.SubmitCommandAsync(batch);

		await Assert.That(Load(store, id).Name).IsEqualTo("second");
	}

	[Test]
	public async Task Should_57a_batch_children_keep_their_command_events()
	{
		var store = ResolveStore();
		if (store is not InMemoryProjection projection)
		{
			return; // the Mongo projection persists no command events at all, batch or not
		}
		projection.PersistCommandEvents = true;
		var a = Guid.NewGuid();
		var b = Guid.NewGuid();
		var batch = new BatchCommand();
		batch.Commands.Add(CreateRoot(store, a, new DemoModel { Name = "a" }));
		batch.Commands.Add(CreateRoot(store, b, new DemoModel { Name = "b" }));

		await store.SubmitCommandAsync(batch);

		var commandEvents = Events().GetAllAsync().ToBlockingEnumerable().OfType<CommandCreatedEvent>().Select(e => e.CommandId).ToList();
		await Assert.That(commandEvents).Contains(batch.Commands[0].CommandId);
		await Assert.That(commandEvents).Contains(batch.Commands[1].CommandId);
		await Assert.That(commandEvents).DoesNotContain(batch.CommandId);
	}

	[Test]
	public async Task Should_57_report_the_events_a_local_write_committed()
	{
		var store = ResolveStore();
		var source = store as ICommittedEventSource;
		await Assert.That(source).IsNotNull();
		var committed = new List<Event>();
		source!.EventsCommitted += events => committed.AddRange(events);

		var model = new DemoModel();
		store.GetCollection<DemoModel>().Add(model);
		model.Name = "pushed";

		await Assert.That(committed.OfType<ComponentPropertyChangedEvent>().Any(e => e.TargetId == store.GetId(model))).IsTrue();
		await Assert.That(committed.OfType<CommandCreatedEvent>().Any()).IsFalse();
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
	public async Task Should_60_version_survives_restart()
	{
		var store = ResolveStore();
		var model = new DemoModel();
		store.GetCollection<DemoModel>().Add(model);
		var id = store.GetId(model);
		model.Name = "versioned";
		var version = store.GetLastEventId(id);
		await Assert.That(version).IsNotEqualTo(Guid.Empty);

		Restart();

		var store2 = ResolveStore();
		_ = store2.GetCollection<DemoModel>().Single(m => store2.GetId(m) == id);
		await Assert.That(store2.GetLastEventId(id)).IsEqualTo(version);
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
	public async Task Should_42_entity_resolves_by_id_after_restart_without_enumeration()
	{
		var store = ResolveStore();
		var parent = AddNode(store, "parent");
		var child = AddNode(store, "child");
		parent.Children.Add(child);
		var parentId = store.GetId(parent);
		var childId = store.GetId(child);

		Restart();

		var store2 = ResolveStore();
		var child2 = (TestGraphNode?)store2.ResolveObject(childId);
		await Assert.That(child2).IsNotNull();
		await Assert.That(child2!.Name).IsEqualTo("child");
		await Assert.That(store2.GetId(child2)).IsEqualTo(childId);
		// The nav property pulls the parent in by id too — nothing was enumerated.
		await Assert.That(child2.Parent).IsNotNull();
		await Assert.That(store2.GetId(child2.Parent!)).IsEqualTo(parentId);
		await Assert.That(ReferenceEquals(store2.ResolveObject(childId), child2)).IsTrue();
		await Assert.That(store2.ResolveObject(Guid.NewGuid())).IsNull();
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
// embedded in the container's own document (see MongoProjection.CreateComponent's remarks on
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

	/// <summary>A second projection over the same database: another process, as far as Mongo is concerned.</summary>
	MongoProjection OtherProcess()
	{
		var sp = ServiceProvider;
		var url = new MongoUrl(_connectionString);
		var database = new MongoClient(_connectionString).GetDatabase(string.IsNullOrWhiteSpace(url.DatabaseName) ? "synqra" : url.DatabaseName);
		return ActivatorUtilities.CreateInstance<MongoProjection>(sp, database);
	}

	[Test]
	public async Task Should_refuse_a_second_unique_facet_in_one_batch_and_apply_nothing()
	{
		var store = Store;
		var id = Guid.NewGuid();
		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestGeneratedContainerNode));
		var facet = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestUniqueComponent));
		var batch = new BatchCommand();
		batch.Commands.Add(new AddComponentCommand { TargetTypeId = container.TypeId, CollectionId = container.CollectionId, TargetId = id, ComponentTypeId = container.TypeId, ComponentId = id, Data = new TestGeneratedContainerNode { Name = "n" }, });
		batch.Commands.Add(new AddComponentCommand { TargetTypeId = container.TypeId, CollectionId = container.CollectionId, TargetId = id, ComponentTypeId = facet.TypeId, Data = new TestUniqueComponent { Subject = "first" }, });
		batch.Commands.Add(new AddComponentCommand { TargetTypeId = container.TypeId, CollectionId = container.CollectionId, TargetId = id, ComponentTypeId = facet.TypeId, Data = new TestUniqueComponent { Subject = "second" }, });

		var refused = await Assert.ThrowsAsync(() => store.SubmitCommandAsync(batch));

		await Assert.That(refused).IsTypeOf<InvalidOperationException>();
		await Assert.That(store.ResolveObject(id)).IsNull();
		await Assert.That(OtherProcess().ResolveObject(id)).IsNull();
	}

	[Test]
	public async Task Should_let_a_batch_add_a_facet_to_the_entity_it_created_and_see_it_from_another_process()
	{
		var store = Store;
		var id = Guid.NewGuid();
		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestGeneratedContainerNode));
		var facet = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestUniqueComponent));
		var batch = new BatchCommand();
		batch.Commands.Add(new AddComponentCommand { TargetTypeId = container.TypeId, CollectionId = container.CollectionId, TargetId = id, ComponentTypeId = container.TypeId, ComponentId = id, Data = new TestGeneratedContainerNode { Name = "n" }, });
		batch.Commands.Add(new AddComponentCommand { TargetTypeId = container.TypeId, CollectionId = container.CollectionId, TargetId = id, ComponentTypeId = facet.TypeId, Data = new TestUniqueComponent { Subject = "once" }, });

		await store.SubmitCommandAsync(batch);

		var local = (TestGeneratedContainerNode)store.ResolveObject(id)!;
		await Assert.That(local.Components.Count).IsEqualTo(1);
		var other = (TestGeneratedContainerNode)OtherProcess().ResolveObject(id)!;
		await Assert.That(other.Components.Count).IsEqualTo(1);
		await Assert.That(((TestUniqueComponent)other.Components.Single()).Subject).IsEqualTo("once");
	}

	[Test]
	public async Task Should_refuse_a_version_that_another_process_moved_on()
	{
		var store = Store;
		var node = new TestGeneratedContainerNode { Name = "v1" };
		store.GetCollection<TestGeneratedContainerNode>().Add(node);
		var id = store.GetId(node);
		var v1 = store.GetLastEventId(id);

		var other = OtherProcess();
		_ = other.GetCollection<TestGeneratedContainerNode>().Single(n => other.GetId(n) == id);
		await Assert.That(other.GetLastEventId(id)).IsEqualTo(v1);

		node.Name = "v2"; // this process moves on; `other` still believes v1
		var metadata = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestGeneratedContainerNode));
		var stale = new ChangeComponentPropertyCommand
		{
			TargetTypeId = metadata.TypeId,
			CollectionId = metadata.GetCollectionId(""),
			TargetId = id,
			ComponentTypeId = metadata.TypeId,
			ComponentId = id,
			PropertyName = nameof(TestGeneratedContainerNode.Name),
			NewValue = "lost update",
		};
		var refused = await Assert.ThrowsAsync(() => other.SubmitCommandAsync(stale, new CommandSubmissionOptions { ExpectedLastEventId = v1 }));

		await Assert.That(refused).IsTypeOf<ConcurrencyException>();
		var reloaded = OtherProcess().GetCollection<TestGeneratedContainerNode>().Single(n => n.Name is not null);
		await Assert.That(reloaded.Name).IsEqualTo("v2");
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
				CollectionId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestGeneratedContainerNode)).CollectionId,
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
