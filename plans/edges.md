## Plan: Edges — generic typed relations in the framework

Replace the reverted `Wire` (one over-specialized relation type) with a small framework
primitive that every consumer's relation kind derives from. The design is validated by two
predecessors: Quotaly's `Wire` (reified edge with component-port endpoints, but hardcoded to
one shape) and the Todo solution's `IHierarchy`/`IMHierarchy`/`IMRHierarchy`/`IDependable`
families (four hand-specializations of "directed reified edge + adjacency views", each paying a
full materialization/clone/hash/dual-direction tax — see `Todo.Model/IHierarchy.cs`,
`Todo.Model/TodoNode.cs`). This collapses all of that into one base hierarchy plus projection
support.

### Decisions

1. **Edges live in Synqra, ports/routing live in the consumer.** "Wire" conflated three
   concerns: the relationship (universal → framework), the addressing granularity (component
   ports → general `Ref`), and the delivery semantics (`PortType`, runtime routing → consumer).
   Only the first belongs in the framework.
2. **An edge is an entity that rides the existing object lifecycle.** No `AddWireCommand` /
   `WireAddedEvent`. An edge is a `[SynqraModel]` object created via `CreateObjectCommand` /
   `ObjectCreatedEvent` like any other; the projection special-cases anything deriving from
   `Edge` to maintain an adjacency index and structural dedup. This reuses
   replication/concurrency/serialization for free and keeps the command/event surface unchanged.
3. **Directionality is the base class; semantic type is the concrete subclass.** Two bases —
   `DirectedEdge` and `UndirectedEdge` — differ only in how endpoints fold into the *structural
   key*. The concrete subclass (the `_t` discriminator) is what distinguishes "depends-on" from
   "parent-of"; it is not a new base class per relation kind.
4. **Endpoints are a `Ref`, not a node id.** A `Ref` addresses an object, optionally narrowed to
   a component and port. Node-to-node is the degenerate case (component fields empty). This
   subsumes both Todo (`ParentId`/`ChildId` = object-only `Ref`) and Wire (full component-port
   `Ref`).
5. **Store one directed edge; expose named collections over the index.** The "collection types"
   ergonomic (`node.Children`, `node.BlockedBy`) is preserved by *generating* adjacency-backed
   collection views — the same way Synqra already generates component collections — instead of
   hand-rolling dual-direction id+object lists with a `_lookup` toggle.

### Separate the two equalities (the core correctness point)

Todo's `IRelation.AddHash` folds `Id, ParentId, ChildId` — i.e. it hashes by **entity
identity**, so it can never answer "is there already a parent edge A→B?". It never needed to.
The framework needs both, kept distinct:

- **Entity identity** = `EdgeId` (a Guid). Used by the log for update/delete. Always by id.
- **Structural key** = identity-independent dedup/upsert key. *This* is where directionality
  matters: directed folds endpoints ordered, undirected folds them as an unordered pair.

### `Ref` — endpoint value type

```csharp
namespace Synqra;

/// <summary>
/// A point in the model graph an edge can terminate at. Addresses an object, optionally
/// narrowed to a component and a named port. Object-to-object links leave the component
/// fields default. Value-based equality so it can key adjacency tables.
/// </summary>
public readonly record struct Ref(
    Guid ObjectId,
    Guid ComponentTypeId = default,
    Guid ComponentId = default,
    string? Port = null)
{
    public bool IsObjectScoped => ComponentTypeId == default && ComponentId == default && string.IsNullOrEmpty(Port);
    public bool IsDefault => ObjectId == default && IsObjectScoped;
}
```

### `Edge` / `DirectedEdge` / `UndirectedEdge`

Note the generator constraint (AGENTS.md "convenience property must NOT be persisted"): the
model-binding/SBX generators store scalar columns, not structs. So — exactly as the reverted
`Wire` did — flatten the two `Ref`s into scalar `[SynqraModel]` columns and expose `A`/`B` as
**read-only expression-bodied** `Ref` accessors (the generator skips those, so they are not
stamped into `[Schema]`). Do **not** hand-write `[Schema]` versions; the generator stamps them.

```csharp
namespace Synqra;

/// <summary>
/// Base for a reified relation between two points in the model graph. An edge is a first-class
/// object (its own EdgeId, its own collection) so it carries identity, properties and the full
/// object lifecycle. Endpoints are stored flattened and surfaced as <see cref="A"/>/<see cref="B"/>.
/// </summary>
[SynqraModel]
public abstract partial class Edge : IIdentifiable<Guid>
{
    public partial Guid EdgeId { get; set; }

    // Endpoint A (flattened — generator stores scalars, not the Ref struct).
    public partial Guid AObjectId { get; set; }
    public partial Guid AComponentTypeId { get; set; }
    public partial Guid AComponentId { get; set; }
    public partial string? APort { get; set; }

    // Endpoint B.
    public partial Guid BObjectId { get; set; }
    public partial Guid BComponentTypeId { get; set; }
    public partial Guid BComponentId { get; set; }
    public partial string? BPort { get; set; }

    Guid IIdentifiable<Guid>.Id => EdgeId;

    /// <summary>Endpoint A as a value tuple. Read-only ⇒ not persisted, not in [Schema].</summary>
    public Ref A => new(AObjectId, AComponentTypeId, AComponentId, APort);
    public Ref B => new(BObjectId, BComponentTypeId, BComponentId, BPort);

    /// <summary>Identity-independent dedup/upsert key. Directionality decides the fold.</summary>
    public abstract EdgeKey StructuralKey { get; }
}

/// <summary>Edge where A→B differs from B→A. A = source, B = target. Ordered structural key.</summary>
[SynqraModel]
public abstract partial class DirectedEdge : Edge
{
    public override EdgeKey StructuralKey => EdgeKey.Directed(GetType(), A, B);
}

/// <summary>
/// Edge where {A,B} == {B,A}. Endpoints are normalized to canonical order at write time so the
/// stored event is deterministic and the index needs only a single insertion. Unordered key.
/// </summary>
[SynqraModel]
public abstract partial class UndirectedEdge : Edge
{
    public override EdgeKey StructuralKey => EdgeKey.Undirected(GetType(), A, B);
}
```

`EdgeKey` does the folding and is the dedup currency:

```csharp
namespace Synqra;

public readonly record struct EdgeKey
{
    private readonly Type _edgeType;
    private readonly Ref _x;   // for directed: source; for undirected: the canonical-lesser endpoint
    private readonly Ref _y;

    private EdgeKey(Type edgeType, Ref x, Ref y) { _edgeType = edgeType; _x = x; _y = y; }

    public static EdgeKey Directed(Type edgeType, Ref source, Ref target)
        => new(edgeType, source, target);

    public static EdgeKey Undirected(Type edgeType, Ref a, Ref b)
        => RefOrder.Compare(a, b) <= 0 ? new(edgeType, a, b) : new(edgeType, b, a);

    // record-struct equality/hash over (_edgeType, _x, _y) gives:
    //   directed   → ordered identity (A→B ≠ B→A)
    //   undirected → unordered identity (already canonicalized)
}
```

### Projection: adjacency index (replaces Todo's `_lookup`)

When the projection materializes/deletes an object that is an `Edge`, it maintains an index in
addition to the normal object collection. This is exactly the reverted `_wiresFrom`/`_wiresTo`
pattern, generalized off `Ref` and gated by structural dedup.

```csharp
namespace Synqra;

public interface IEdgeIndex
{
    /// <summary>Directed: edges whose source is <paramref name="a"/>. Undirected: incident to a.</summary>
    IReadOnlyList<Edge> EdgesFrom(Ref a);
    /// <summary>Directed: edges whose target is <paramref name="b"/>. Undirected: incident to b.</summary>
    IReadOnlyList<Edge> EdgesTo(Ref b);
    IReadOnlyList<Edge> EdgesBetween(Ref a, Ref b);
    bool TryGetByKey(EdgeKey key, out Edge edge);
}
```

- Insertion checks `StructuralKey`; a duplicate is rejected (surfaced like an
  `ICanAddComponent` veto rather than thrown, so replay is idempotent).
- The inverse direction is a *query*, never a second stored list — this removes Todo's
  `Parents`+`Children` / `BlockedBy`+`Blocks` dual-write and its consistency bugs.

### Generated collection views (the "collection types" bridge)

The consumer keeps `node.Children` ergonomics; the generator backs the property with an index
query instead of a hand-rolled lazy list. One attributed partial property replaces ~40 lines of
`…Ids` / `…Created` / `…UnsetIfEmpty` / `_lookup` plumbing per direction.

```csharp
[SynqraModel]
public partial class TodoNode
{
    [EdgeCollection(typeof(HierarchyEdge), End.Source)]   // this node is the source ⇒ its children
    public partial IReadOnlyCollection<TodoNode> Children { get; }

    [EdgeCollection(typeof(HierarchyEdge), End.Target)]   // this node is the target ⇒ its parents
    public partial IReadOnlyCollection<TodoNode> Parents { get; }

    [EdgeCollection(typeof(DependsOn), End.Target)]
    public partial IReadOnlyCollection<TodoNode> BlockedBy { get; }
}
```

### Constraints & ordering

- **Tree = directed edge + uniqueness.** `IHierarchy` (single-parent) is `HierarchyEdge` with
  "≤ 1 inbound edge per node", expressed as an edge-level constraint
  (e.g. `[EdgeConstraint(MaxInbound = 1)]`). `IMHierarchy`/`IMRHierarchy` are the same edge
  without the constraint.
- **Order belongs on the edge, not the node.** Todo's `IOrderable.Order` sits on the node, which
  is a single-parent assumption leaking — in a multi-parent graph a child's sibling order differs
  per parent. Put `double Order` on `HierarchyEdge`. (Sibling ordering then reads off the inbound
  edge for a given parent.)

### Old → new mapping

| Predecessor | New |
|---|---|
| Todo `IRelation` (`Id`, `ParentId`, `ChildId`, `Extra`) | `DirectedEdge` subclass (`EdgeId`, `A`/`B`, derived props) |
| Todo `ParentId`/`ChildId` (object-only) | `Ref` with only `ObjectId` set |
| Todo `IHierarchy` (single parent) | `HierarchyEdge` + `MaxInbound = 1` |
| Todo `IMHierarchy`/`IMRHierarchy` | `HierarchyEdge`, no constraint |
| Todo `IDependable` (blocks/blockedBy) | `DependsOn : DirectedEdge`; `Blocks`/`BlockedBy` = `EdgesFrom`/`EdgesTo` |
| Todo `_lookup`/`FindRelsByIds` + dual id-lists | `IEdgeIndex` + generated `[EdgeCollection]` views |
| Quotaly `Wire` (component-port endpoints) | `GraphWire : DirectedEdge` adding `Port`/`PortType` (consumer) |
| Quotaly `PortRef` | framework `Ref` |
| Quotaly `PortType`, runtime routing | stays in the consumer's graph runtime |

### Steps

1. Add `Ref` and `EdgeKey` (+ `RefOrder` canonical comparer) to `Synqra.Model`.
2. Add `Edge` / `DirectedEdge` / `UndirectedEdge` `[SynqraModel]` bases (flattened endpoints,
   read-only `A`/`B`, abstract `StructuralKey`). Let the generator stamp `[Schema]`.
3. Teach `InMemoryProjection` to recognize `Edge`-derived objects on
   create/delete and maintain `IEdgeIndex` + structural dedup (port the reverted
   `_wiresFrom`/`_wiresTo` code off `Ref`).
4. Add the `[EdgeCollection]` attribute + generator support for adjacency-backed read-only
   collection properties.
5. Port a vertical slice to prove it: re-express Quotaly's scene links as a
   `SceneLink : DirectedEdge` (or `UndirectedEdge`) in the consumer; re-express SimpleV1's wire
   as `GraphWire : DirectedEdge` with `Port`/`PortType` consumer-side.
6. (Deferred) Cascade-on-endpoint-delete — depends on object-delete existing in the projection
   (currently a no-op; see `NodesController` "node deletion intentionally absent" comment). Until
   then, queries filter edges whose endpoints no longer resolve (as Quotaly's `IsOwnedBy` already
   did).

### Open questions (need a decision before coding)

1. **Serialization of endpoints** — confirm we flatten to scalars now (recommended, matches
   `Wire`), versus teaching the SBX generator a `Ref` column type. Flatten now, revisit later.
2. **Dedup placement** — reject duplicate `StructuralKey` in the projection (idempotent replay,
   recommended) vs. throwing from the add path.
3. **Undirected normalization** — canonicalize `A`/`B` at construction (deterministic event,
   recommended) vs. only at index/compare time.
4. **Scope of v1** — ship `Ref` + edge bases + index + generated collections; defer constraints
   (`MaxInbound`) and cascade. Agree the cut line.
