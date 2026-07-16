# Synqra — Model Substrate

The layer **after `core.md`**. `core.md` defines the non-negotiable invariants (event log is
truth, state is a derived cache, deterministic replay, virtual synchrony). This document defines
the **concrete model substrate** those invariants are expressed in: entities, components, links,
and values — how they are identified, mutated, stored, and navigated.

> **Status:** this is the **target design** for the ECS refactor (collapsing the historical
> Objects / Components / Links split into one substrate). The codebase is converging on it; some
> areas may still show the older three-subsystem shape until their phase lands. There is **no
> upgrade from previous persisted state** — stores are rebuilt fresh, so this describes the end
> state directly, not a migration.

---

## 1. The substrate (ECS)

Synqra's model is **Entity–Component** (the data-model shape of ECS; not its system-scheduling
machinery):

- **Entity** — a bare identity (a `Guid`). Holds no data itself.
- **Component** — a typed unit of data (and optional behaviour) attached to exactly one entity.
  **Everything that carries data is a component**, including the entity's own "primary" data.
- **Link** — a component with a `Target`: a reified relationship from its owner entity to another
  addressable thing. Links are components, not a separate kind.
- **Value** — Tier-B leaf data (`int[]`, small value structs) stored **inline** in a component's
  data, never an entity of its own.

There is **one data mechanism**: components. Links are components; an object's data is a
component. This is the whole point of the substrate — a single uniform mechanism for storage,
mutation, replication, and navigation.

## 2. Identity & addressing

- **Unified id space.** Entities and components share one `Guid` id space (v7 for data). A
  reference is *just a `Guid`*; it can address an entity or a component without a discriminator.
- **Root / existence component.** Each entity's primary data lives in a **root component** with
  the invariant **`_id == _eid == entityId`** (`_eid` = the owning entity id; named `_eid` — not
  `_oid` — because "OID" collides with the object identifier of the security/X.509 RFCs, and
  "entity" is the ECS term). The root component *is*
  the entity's existence: an entity exists iff its root component is present.
- **Component id.** Every component has a first-class `_id` — including unique ones. **Uniqueness
  is a max-cardinality constraint, not an identity-suppressor** (a component can be "at most one
  of this type per entity" *and* still have its own id).
- **`_t`** — the type discriminator (component/link concrete type name). `GetCollection<T>()` is a
  `_t == "T"` query over the root components; there is no separate per-type collection and **no
  `CollectionId`**.
- **`_sid`** — stream id, present on *everything* (events, commands, components). A stream is a
  mandatory security boundary; it is orthogonal to entity/component identity.
- **`Target`** (links only) — a single first-class, indexed `Guid` naming the other endpoint. No
  `TargetKind`: "target an entity" means "target its root component"; targeting a non-root
  component or another link works for free (link-to-component, link-to-link).

Persisted component document shape (single collection): `{ _id, _t, _sid, _eid, ...data, Target? }`.

## 3. Components

- Attach/detach via `AddComponentCommand` / `DeleteComponentCommand`; edit via
  `ChangeComponentPropertyCommand`. Each produces a matching event
  (`ComponentAddedEvent` / `ComponentDeletedEvent` / `ComponentPropertyChangedEvent`).
- **Uniqueness & veto** (arity): `[Component(IsUnique = true)]` on a type or interface caps it at
  one-per-entity; `ICanAddComponent` lets existing components veto an incoming peer. Enforced in
  `ComponentsCollection` at event-apply time. This is how "single-parent vs multi-parent" is
  expressed — a constraint toggle, never a schema change.
- **Component ids are stable across edits** — editing is in-place `ChangeComponentPropertyCommand`
  (same id), never delete+recreate. This is what keeps link `Target`s from dangling on edit.
- The generator emits a `Components` collection for an `IComponentContainer`, routing user-driven
  `.Add`/`.Remove` through the command channel (`StoreBoundComponentsCollection`).

## 4. Links (components with a `Target`)

- A link is a component whose type derives from a `Link` base and carries a `Target`. Its owner
  `_eid` is the **source**; for undirected links the owner is the **canonical `min(A, B)`**
  endpoint (deterministic, so concurrent create-from-both-ends converges — conflict-free dedup).
- **Store-once + reverse-view.** A link is stored exactly once (on the owner). The reverse
  direction is a **query**, never a stored reciprocal: outbound = `_eid == me`, inbound =
  `Target == me`. This is the adjacency index (`ILinkIndex`), and it removes the dual-write /
  drift class of bugs entirely.
- **Entity identity vs structural key.** A link has its own `_id` (used to address it for
  update/delete) *and* a `LinkKey` — the identity-independent dedup key (directed folds endpoints
  ordered; undirected folds them via `min`). Keep both distinct.
- **Navigation** is generated from `[To]` (links from me → their targets), `[From]` (links at me →
  their sources), `[Related]` (undirected, either side). Element type may be the node (primitive
  link, no payload) or the `Link` itself (payload-carrying link). `ILinkAware` fires
  `INotifyPropertyChanged` on both endpoints when an incident link changes (nav props are live
  queries with no backing field).
- **One domain event per link mutation** — the reverse side is a projection-maintained view, not a
  second event.

## 5. Commands & events (two logs)

- **Command log** — *intent*: undo/redo granularity, action history, and compact replication
  transport (client-generated ids make command→event expansion deterministic). Commands may be
  rejected; they are not history.
- **Event log** — *fact*: the deterministic expansion, immutable and append-only, that projections
  fold. State is a derived cache of the event log.
- A rich command (e.g. a reparent) expands to a small set of **structural** events, applied as one
  **atomic event-group fold** (consistency comes from the atomic batch, not from coarser events).
- The concrete component/link type travels as **data** (`_t` / type-id), never as an event-schema
  branch — the projection folds structurally and never switches on a concrete domain type.
- The **CommandVisitor** (command→event generation) is where invariants harden: existence guard
  (reject a link/change against a non-existent id), arity/uniqueness, undirected canonical-owner
  dedup, and cascade DAG-safety.

## 6. Existence, delete, roots

- **Existence = the root component (`_id == entityId`) is present.** A write against an id with no
  live root component fails the consistency guard.
- **Delete is explicit and visible.** Every removal emits an event (`ComponentDeletedEvent`);
  nothing is silently hidden. Deleting an entity deletes its root component and **cascades** its
  components and incident links under a **DAG guarantee** (the cascade terminates). No tombstones.
- **Root entity.** Each stream has a definitional, well-known **root instance** — a reserved v8
  `C0DE` class id (see §8). It is a root *entity* (reachability origin), explicitly **not** the
  retired root-*stream* concept. "Rooting" an object = a link from the real container entity
  (tenant / workspace / user), itself transitively linked to the root. No synthetic per-object
  root.

## 7. GC — error-detector only (for now)

GC is currently a **diagnostic**, not a collector: a scan over the durable projection reporting
components/entities that are **neither reachable-from-root nor explicitly deleted** (leaks /
invariant violations). No physical collection, no tombstones.

**Deferred (parked, do not build yet):** tombstoning (`_deleted`), physical GC / reclamation, the
homomorphic world-hash over the reachable set, snapshot/compaction horizon. When revived: the
alive-set must stay *explicit* (tombstone transitions), never inferred from lazy reachability —
that is the only form that marries with real-time world-hashing (see Historical §H).

## 8. Ids (v7 data, v8 system)

- **v7 GUIDs** for all data (entities, components, links) — monotonic, totally ordered (undirected
  `min` folding relies on this order).
- **Event ids are derived, not random.** Each event a command expands to gets `Derive(CommandId,
  ordinal)` — the command's client-generated v7 id with its low random bits incremented by a small
  per-command ordinal (the wrapper `CommandCreatedEvent` = ordinal 0, domain events start at 1).
  This makes the command→event expansion reproducible across nodes and replays (core.md §8) with no
  clock or shared counter, and the derived id stays a time-ordered v7 that sorts adjacent to its
  command. (Modelled on the Todo predecessor's id layout: a reserved low-bytes counter region +
  increment.)
- **v8 `C0DE…` GUIDs** for well-known/system ids (RFC 9562, application-defined layout — authoritative
  source `Synqra.Model/SynqraGuids.cs`). The version nibble structurally marks "system/well-known" vs
  v7 data. Layout `C0DE yyyy-yyyy 8Sxx 8CCC iiii…`:
  - **`C0DE`** — magic prefix (hex-readable "CODE"); marks a custom/system UUID at a glance.
  - **`yyyy-yyyy`** — project hash: first 4 bytes of SHA-256 of the lowercase project name
    (`synqra` → `ADD0 1032`). All-zero here = **internal** (framework/infrastructure, no external
    company); a non-zero hash = an external company/project.
  - **`8S`** version + sub-version — RFC 9562 v8 (`8`), then a sub-version nibble that is the
    **prod/test** flag: `0` ⇒ **prod**, `1` ⇒ **test**. Orthogonal to the company field, so all four
    combinations exist (internal-prod, internal-test, external-prod, external-test). Higher
    sub-versions reserved.
  - **`8CCC`** — RFC variant (`0b10`, renders as hex `8`) + a 12-bit **class** (up to 4096). Well-known
    classes: `000` object-type namespace, `005` container/stream, `00C` command, `00E` event; the root
    entity is a new class here.
  - trailing bytes — instance / counter.
- **Fixed test guids stay RFC-valid** — never the all-zero `00000000-0000-0000-0000-…` (version 0, not
  a legal UUID). Use the internal-test well-known form `00000000-C0DE-8001-8CCC-…` (company `00…` =
  internal, sub-version `1` = test): `…-800E-…` events, `…-800C-…` commands, `…-8005-…`
  containers/stream ids. The all-zero **instance** tail (`…-000000000000`) is reserved for unit-test
  fixtures.
- **Retired:** the default/root **stream** reservation — a stream id is mandatory and has no default.
  The **MasterId** scheme (term/sequence/collection ordering) is not used — there is no master
  election or monotonic cluster clock.

## 9. Storage & projections

- **One shared `Components` collection** per stream, `_sid`-scoped: `{ _id, _t, _sid, _eid, …,
  Target? }`. Root components (`_id == _eid`) are the entities; other components hang off `_eid`;
  links carry `Target`. No per-type object collections; no separate `Links` collection.
- **Projections are derived and rebuildable** (core.md §4). Different backends may differ in
  physical layout; the event log is the invariant.
- **Backends:** InMemory / Mongo / Sqlite / File. Parity is required; where a backend cannot yet
  serve a capability it must guard explicitly, not silently no-op.
- **Ceremony:** every command/event/component/link type keeps its `[Schema(...)]` version chain
  and is registered for SBX (binary), Mongo class maps, and Native-AOT `JsonSerializerContext`.

---

## Historical / superseded ideas

Kept so older decisions stay documented and are not re-litigated. **None of the following is
current** — they describe earlier states or roads not taken.

- **Three separate subsystems (Objects / Components / Links).** Objects rode
  `CreateObjectCommand` in per-type collections; links rode a dedicated `AddLinkCommand` parallel
  to components. Superseded by the single-component substrate (§1). The reasons the earlier
  links-as-own-kind decision gave (a link is "an attached fact, not a top-level object"; atomic
  both-endpoints event; a clean hook for `ILinkAware`) are all preserved by links-as-components —
  a link is still an attached fact with one atomic add and a well-defined change moment.
- **`Ref` value type / component-port endpoints.** An earlier `Edge`/`Ref` design flattened
  `(ObjectId, ComponentTypeId, ComponentId, Port)` into endpoints to address component ports.
  Dropped then as "node-to-node is all the framework needs." Component-level targeting is now back
  — but as a **bare `Guid` in the unified id space**, not a `Ref` struct, so the complexity that
  motivated dropping it does not return.
- **`TargetKind` discriminator.** Considered for entity-vs-component targets. Unnecessary once the
  root-component invariant (`_id == entityId`) makes a single `Guid` address anything.
- **Stored `_primary` flag / dedicated `ExistComponent` type.** The primary/root component is
  simply the one where `_id == _eid`; no stored flag and no privileged type are needed.
- **Root *stream*.** A reserved default/root stream id was removed; streams are mandatory and have
  no default. The current **root *entity*** is a different concept (§6).
- **`LinkReparented` / per-edge cascade events / auto-maintained reciprocal component.** Rejected:
  reparent stays a rich command expanding to structural events; cascade is reachability/DAG-based,
  not per-edge; the reverse direction is a query, never a stored reciprocal.
- **H. GC married to real-time hashing via lazy reachability.** Rejected: lazy reachability GC is
  eventual/local and cascades fuzzily, so it cannot back a real-time exact agreement hash. The
  viable form (deferred) is an *explicit* alive-set via eager cascade-tombstone under a DAG
  guarantee, with an incremental homomorphic (LtHash-style) hash over the non-tombstoned set;
  physical reclamation stays separate and never touches the hash; resurrection = a projection that
  ignores tombstones.
