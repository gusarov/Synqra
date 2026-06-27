## Plan: Links — generic typed relations in the framework

Replace the reverted `Wire` (one over-specialized relation type) with a small framework
primitive that every consumer's relation kind derives from. The design is validated by two
predecessors: Quotaly's `Wire` (reified edge with component-port endpoints, but hardcoded to
one shape) and the Todo solution's `IHierarchy`/`IMHierarchy`/`IMRHierarchy`/`IDependable`
families (four hand-specializations of "directed reified edge + adjacency views", each paying a
full materialization/clone/hash/dual-direction tax — see `Todo.Model/IHierarchy.cs`,
`Todo.Model/TodoNode.cs`). This collapses all of that into one base hierarchy plus projection
support.

This is the as-built doc — it tracks `Synqra.Model/Links/*`, not the original sketch. Two
decisions changed after the first pass landed (see "What changed after v1" below); read that
section if you remember an earlier `Ref`/`Edge` design.

### Decisions

1. **Links live in Synqra, ports/routing live in the consumer.** "Wire" conflated three
   concerns: the relationship (universal → framework), the addressing granularity (component
   ports → consumer-side concept), and the delivery semantics (`PortType`, runtime routing →
   consumer). Only the first belongs in the framework.
2. **A link is an attached fact, not a top-level object.** It does NOT ride
   `CreateObjectCommand`/`ObjectCreatedEvent`. It has its own dedicated
   `AddLinkCommand`/`LinkAddedEvent` and `RemoveLinkCommand`/`LinkRemovedEvent` — the same way a
   component has `AddComponentCommand`/`ComponentAddedEvent` instead of riding the generic object
   lifecycle. Carrying both endpoints in one atomic event also removes the reconstruction window
   the generic object path has (where a property arrives in a separate event after creation):
   `SourceId`/`TargetId` are always already set the moment a `LinkAddedEvent` is materialized.
3. **Directionality is the base class; semantic type is the concrete subclass.** Two bases —
   `DirectedLink<TSource, TTarget>` and `UndirectedLink<TSource, TTarget>` — differ only in how
   endpoints fold into the *structural key*. The concrete subclass (the `_t` discriminator) is
   what distinguishes "depends-on" from "parent-of"; it is not a new base class per relation kind.
4. **Endpoints are plain object ids, typed at the generic-parameter level.** No port/component
   addressing in the framework base — `Link.SourceId`/`TargetId` are scalar `Guid` columns, and
   `Link<TSource, TTarget>.Source`/`Target` resolve them through the store into the consumer's own
   model types. Node-to-node is the only case the framework knows about; component/port
   granularity (Quotaly's `Wire` shape) stays a consumer-side concern layered on top if/when
   needed.
5. **Store one directed link; expose named navigation properties over the index.** The
   "collection types" ergonomic (`node.Children`, `node.BlockedBy`) is preserved by *generating*
   adjacency-backed navigation properties — the same way Synqra already generates component
   collections — instead of hand-rolling dual-direction id+object lists with a `_lookup` toggle.

### Separate the two equalities (the core correctness point)

Todo's `IRelation.AddHash` folds `Id, ParentId, ChildId` — i.e. it hashes by **entity
identity**, so it can never answer "is there already a parent link A→B?". It never needed to.
The framework needs both, kept distinct:

- **Entity identity** = `LinkId` (a Guid). Used by the log for update/delete (`RemoveLinkCommand`
  addresses by this, via `ILinkIndex.TryGetById`). Always by id.
- **Structural key** = identity-independent dedup/upsert key (`LinkKey`). *This* is where
  directionality matters: directed folds endpoints ordered, undirected folds them as an unordered
  pair.

### `Link` / `Link<TSource, TTarget>` / `DirectedLink<,>` / `UndirectedLink<,>`

`Link` is the non-generic base the projection and index work with; `Link<TSource, TTarget>` adds
the typed, store-resolved `Source`/`Target` accessors a consumer's concrete link subclasses
inherit. These accessors are `[JsonIgnore]` and read-only-by-resolution — the generator only
persists the scalar `SourceId`/`TargetId` columns it stamps into `[Schema]`.

```csharp
namespace Synqra;

[SynqraModel]
[Schema(2026.500, "1 LinkId Guid SourceId Guid TargetId Guid")]
public abstract partial class Link : IIdentifiable<Guid>
{
    public partial Guid LinkId { get; set; }   // own identity, not store-assigned
    public partial Guid SourceId { get; set; }
    public partial Guid TargetId { get; set; }

    Guid IIdentifiable<Guid>.Id => LinkId;

    public abstract System.Type SourceType { get; }
    public abstract System.Type TargetType { get; }
    public abstract LinkKey StructuralKey { get; }

    protected object? ResolveEndpoint(System.Guid id) =>
        id == default ? null : ((IBindableModel)this).Store?.ResolveObject(id);
}

public abstract class Link<TSource, TTarget> : Link
    where TSource : class
    where TTarget : class
{
    public override System.Type SourceType => typeof(TSource);
    public override System.Type TargetType => typeof(TTarget);

    [System.Text.Json.Serialization.JsonIgnore]
    public TSource? Source { get => (TSource?)ResolveEndpoint(SourceId); set => SourceId = IdOf(value); }

    [System.Text.Json.Serialization.JsonIgnore]
    public TTarget? Target { get => (TTarget?)ResolveEndpoint(TargetId); set => TargetId = IdOf(value); }
}

/// <summary>A→B differs from B→A. Source is A, Target is B. Ordered structural key.</summary>
public abstract class DirectedLink<TSource, TTarget> : Link<TSource, TTarget>
    where TSource : class where TTarget : class
{
    public override LinkKey StructuralKey => LinkKey.Directed(GetType(), SourceId, TargetId);
}

/// <summary>{A,B} == {B,A}. Endpoints fold to a canonical order for the structural key only —
/// SourceId/TargetId on the instance keep whatever order the caller set them in.</summary>
public abstract class UndirectedLink<TSource, TTarget> : Link<TSource, TTarget>
    where TSource : class where TTarget : class
{
    public override LinkKey StructuralKey => LinkKey.Undirected(GetType(), SourceId, TargetId);
}
```

A consumer never sees a raw `Guid` or any framework endpoint-reference type — they declare e.g.
`class HierarchyLink : DirectedLink<TodoNode, TodoNode>` and work with `link.Source`/`link.Target`
or, more commonly, never touch a `Link` instance directly at all (see navigation properties below).

`LinkKey` does the folding and is the dedup currency — a value type so it can key the adjacency
index by value, comparing `(linkType, x, y)` where directed keeps `x`/`y` as given and undirected
canonicalizes them at construction.

### Projection: adjacency index (replaces Todo's `_lookup`)

The in-memory projection maintains an `ILinkIndex` alongside the normal object collection,
populated only by `LinkAddedEvent`/`LinkRemovedEvent` (not by the generic object-create path —
see decision 2). This is the reverted `_wiresFrom`/`_wiresTo` pattern, generalized and gated by
structural dedup.

```csharp
namespace Synqra;

public interface ILinkIndex
{
    IReadOnlyCollection<Link> Links { get; }
    IReadOnlyList<Link> LinksAt(Guid nodeId, LinkEnd end, Type linkType);
    IReadOnlyList<Link> LinksBetween(Guid a, Guid b, Type linkType);
    bool TryGetByKey(LinkKey key, out Link? link);
    bool TryGetById(Guid linkId, out Link? link);
}
```

- `LinkEnd` is `Source` / `Target` / `Either` — directed lookups filter by source or target side;
  undirected navigation always passes `Either`.
- Insertion checks `StructuralKey` (`VisitLinkAddedCore` in `InMemoryProjection`); a duplicate
  throws `InvalidOperationException` when submitted directly via `AddLinkCommand`, but the
  generated navigation-collection `Add` path pre-checks the index and treats re-adding an
  existing link as a no-op — so replay and "ergonomic" usage both stay idempotent, just at
  different layers.
- The inverse direction is a *query* over the same index, never a second stored list — this
  removes Todo's `Parents`+`Children` / `BlockedBy`+`Blocks` dual-write and its consistency bugs.
- `ILinkAware.OnLinkChanged(Type linkType, LinkEnd selfEnd)` is the piece Todo's design didn't
  need to solve: nav properties like `Children`/`Parent` have no backing field (they're live
  queries), so nothing tells `INotifyPropertyChanged` observers when they change. The generator
  implements `ILinkAware` per class with nav properties, and the projection calls it on both
  endpoints whenever a link is added or removed, translating "a link of this type changed at this
  end" into the matching `OnPropertyChanged(nameof(...))` calls.

### Generated navigation properties (the "collection types" bridge)

`[To]`/`[From]`/`[Related]` replace the sketch's `[EdgeCollection]`. The consumer keeps
`node.Children` ergonomics; the generator backs the property with an index query instead of a
hand-rolled lazy list.

```csharp
[SynqraModel]
public partial class TodoNode
{
    [To(typeof(HierarchyLink))]   public partial ICollection<TodoNode> Children { get; }
    [From(typeof(HierarchyLink))] public partial IReadOnlyList<TodoNode> Parents { get; }
    // Opt-in setter: declare { get; set; } (not { get; }) and the generator emits a setter that
    // replaces whatever single link already occupies that role with a new one (or clears it on
    // null). Never forced — collection-typed properties never get one regardless.
    [From(typeof(HierarchyLink))] public partial TodoNode? Parent { get; set; }

    [To(typeof(DependsOn))]   public partial ICollection<TodoNode> Blocks { get; }
    [From(typeof(DependsOn))] public partial IReadOnlyList<TodoNode> BlockedBy { get; }

    [Related(typeof(RelatedTo))] public partial ICollection<TodoNode> RelatedNodes { get; }

    // Link-typed navigation (element type is the Link itself, not the node) — used when the link
    // carries payload, so the payload stays reachable. LinkType argument becomes optional since
    // it's inferable from the element type.
    [To]   public partial ICollection<WeightedLink> WeightedChildren { get; }
    [From] public partial IReadOnlyList<WeightedLink> WeightedParentLinks { get; }
}
```

`[To]` reads "links originating from me" (declaring node is the source); `[From]` reads "links
pointing at me" (declaring node is the target); `[Related]` is the undirected form (incident
either way). Mutating the collection — `parent.Children.Add(child)` — submits an
`AddLinkCommand` under the hood; `Remove`/`Clear` submit `RemoveLinkCommand`.

### Constraints & ordering (not yet built — still a v2 area)

- **Tree = directed link + uniqueness.** `IHierarchy` (single-parent) would be `HierarchyLink`
  with "≤ 1 inbound link per node", expressed as a link-level constraint
  (e.g. `[LinkConstraint(MaxInbound = 1)]`). `IMHierarchy`/`IMRHierarchy` are the same link
  without the constraint. Today nothing enforces this — `Parent` degrading to "first of
  `Parents`" once there's more than one is the only behavior, by design (see
  `LinksTests.Should_support_multiple_parents_via_the_same_link_type`).
- **Order belongs on the link, not the node.** Todo's `IOrderable.Order` sits on the node, which
  is a single-parent assumption leaking — in a multi-parent graph a child's sibling order differs
  per parent. Put `double? Order` on a payload link like `WeightedLink` instead (sibling ordering
  then reads off the inbound link for a given parent).

### Old → new mapping

| Predecessor | New |
|---|---|
| Todo `IRelation` (`Id`, `ParentId`, `ChildId`, `Extra`) | `DirectedLink<,>` subclass (`LinkId`, `SourceId`/`TargetId`, typed `Source`/`Target`) |
| Todo `ParentId`/`ChildId` (object-only) | `Link.SourceId`/`TargetId` (plain `Guid`, no port/component addressing) |
| Todo `IHierarchy` (single parent) | `HierarchyLink` + (planned) `MaxInbound = 1` |
| Todo `IMHierarchy`/`IMRHierarchy` | `HierarchyLink`, no constraint |
| Todo `IDependable` (blocks/blockedBy) | `DependsOn : DirectedLink<,>`; `Blocks`/`BlockedBy` = `[To]`/`[From]` nav properties |
| Todo `_lookup`/`FindRelsByIds` + dual id-lists | `ILinkIndex` + generated `[To]`/`[From]`/`[Related]` nav properties + `ILinkAware` for change notification |
| Quotaly `Wire` (component-port endpoints) | a consumer-defined `DirectedLink<,>` subclass adding `Port`/`PortType` itself (framework stays node-to-node only) |
| Quotaly `PortRef` | not adopted into the framework — stays a consumer-side concept if/when needed |
| Quotaly `PortType`, runtime routing | stays in the consumer's graph runtime |

### What changed after v1 (read this if you remember `Edge`/`Ref`)

The first pass through this plan used `Edge`/`DirectedEdge`/`UndirectedEdge` names and a `Ref`
value type (`record struct Ref(Guid ObjectId, Guid ComponentTypeId, Guid ComponentId, string?
Port)`) flattened into the base class so endpoints could be either a node or a component port in
one type. Two things changed before this shipped:

1. **`Ref` was dropped entirely.** Once it became clear the framework only needs node-to-node
   endpoints (decision 4 above), `Ref`'s component/port fields had no framework-level consumer —
   they existed purely to subsume Quotaly's `Wire` shape pre-emptively. `Link.SourceId`/`TargetId`
   being plain `Guid` columns is simpler and the same generic-parameter trick
   (`Link<TSource, TTarget>`) gives back full type safety without it. If a consumer ever needs
   component/port-level addressing again, that's a subclass concern, not a framework one.
2. **`Edge` was renamed to `Link`, and stopped riding the generic object lifecycle.** The original
   plan's decision 2 said "an edge is an entity that rides the existing object lifecycle... like
   any other [object]". That turned out to be wrong: a link is conceptually closer to a component
   (an attached fact about a relationship) than to a top-level object, and going through
   `CreateObjectCommand`/`ObjectCreatedEvent` left no clean way to also fire dedicated, typed
   events for "a link was added" — which matters once `ILinkAware` needs a well-defined moment to
   hook into. `AddLinkCommand`/`LinkAddedEvent`/`RemoveLinkCommand`/`LinkRemovedEvent` (mirroring
   `AddComponentCommand`/`ComponentAddedEvent`) replaced it (decision 2 above, current form).

### Gotchas when adding a new link type or backend

These are constraints discovered while building this out, worth knowing before touching link
code again:

- **MongoDB backend:** a link's own concrete type flows through `LinkAddedEvent.Data` as a
  dynamic `object`-typed payload. MongoDB's default `ObjectSerializer` rejects arbitrary
  application types unless explicitly allowed, and implicit auto-mapping (the first time BSON
  encounters a type it was never explicitly `RegisterClassMap`'d for) skips `[JsonIgnore]`
  stripping — both handled centrally in `MongoEventClassMaps.Register()`
  (`PatchObjectSerializerDefaults` + the global `[JsonIgnore]` convention), not per-link-type.
  Nothing to do per new link type on this front.
- **Native AOT (CI):** `Activator.CreateInstance(Type)` needs a type's parameterless-constructor
  reflection-invoke metadata explicitly preserved under Native AOT trimming. Test-only link/node
  types must be added to `SampleJsonSerializerContext`'s `[JsonSerializable(...)]` list (and its
  `_extra` array) the same way `DemoModel` is, or a durable restart-replay test for that type will
  pass under the JIT locally and fail only in the AOT-published CI build.
- **SBX (binary) serializer:** a link type with zero properties of its own beyond the base (e.g.
  a pure marker like `DependsOn`) still needs its own `[SynqraModel]`/`[Schema(...)]` —
  `SbxSerializer.Map` requires schema info on the specific type being mapped, not just on an
  inherited base.

### Steps (status)

1. ~~Add `Ref` and `EdgeKey`~~ — superseded; see "What changed after v1".
2. Add `Link` / `DirectedLink<,>` / `UndirectedLink<,>` `[SynqraModel]` bases — **done**
   (`Synqra.Model/Links/Link.cs`, `Link.Generic.cs`, `DirectedLink.cs`, `UndirectedLink.cs`).
3. Teach `InMemoryProjection` to maintain `ILinkIndex` + structural dedup off
   `AddLinkCommand`/`RemoveLinkCommand` — **done** (`VisitLinkAddedCore`/`VisitLinkRemovedCore`).
4. Add `[To]`/`[From]`/`[Related]` + generator support for adjacency-backed navigation
   properties, including the opt-in setter and `ILinkAware` — **done**
   (`ModelBindingGenerator.cs`, `CodeGenHelpers.cs`).
5. Port a vertical slice to prove it — **not yet done**; the Quotaly-side PR adapting to the
   wires revert chose clean removal over migrating onto `Link`, so there is no consumer slice yet.
6. (Deferred) Cascade-on-endpoint-delete — depends on object-delete existing in the projection
   (currently a no-op). Until then, queries must tolerate links whose endpoints no longer resolve.
7. (Deferred) `MongoProjection`/`SqliteProjection`/file projection currently stub
   `AddLinkCommand`/`RemoveLinkCommand`/`LinkAddedEvent`/`LinkRemovedEvent` (Mongo throws
   `NotImplementedException`; File and Sqlite no-op) — only the in-memory projection materializes
   `ILinkIndex` today.

### Resolved questions

1. **Serialization of endpoints** — flattened to two scalar `Guid` columns (`SourceId`/
   `TargetId`); no `Ref`-like struct at all (see "What changed after v1").
2. **Dedup placement** — both: the projection rejects a duplicate `StructuralKey` submitted via
   `AddLinkCommand` directly (throws, so replay corruption is loud); the generated navigation
   collection pre-checks the index and treats re-adding the same link as a no-op (so ergonomic
   usage stays idempotent without throwing on a harmless double-`Add`).
3. **Undirected normalization** — canonicalized only at `StructuralKey`/index-compare time;
   `SourceId`/`TargetId` on the instance keep whatever order the caller set them in.
4. **Scope of v1** — shipped link bases + index + generated navigation properties + dedicated
   commands/events + `ILinkAware` change notification. Constraints (`MaxInbound`) and cascade
   remain deferred (see Steps 6-7).
