# Synqra — Model Substrate

The layer **after `core.md`**. `core.md` defines the non-negotiable invariants (event log is
truth, state is a derived cache, deterministic replay, virtual synchrony). This document defines
the **concrete model substrate** those invariants are expressed in: entities, components, links,
and values — how they are identified, mutated, stored, and navigated.

> **Status:** this is the **target design** for the ECS refactor (collapsing the historical
> Objects / Components / Links split into one substrate). The codebase is converging on it; some
> areas may still show the older three-subsystem shape until their phase lands. **While this refactor
> is in flight**, persisted state is rebuilt fresh rather than migrated — a temporary convenience, not
> a policy — so this document can describe the end state directly. **Seamless upgrade of persisted
> state is a core Synqra goal**, switched on once the substrate settles; the design does not forgo it.

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
  v7 data. Layout `C0DE yyyy-yyyy 8prs vCCC iiii…` — group-3 `8prs` = version + **project** + **space**;
  group-4 `vCCC` = **env**-variant + a full 3-nibble **class** (project/space were moved into group-3 so
  the class owns all of group-4's tail):
  - **`C0DE`** — magic prefix (hex-readable "CODE"); marks a custom/system UUID at a glance.
  - **`yyyy-yyyy`** — company hash: first 4 bytes of SHA-256 of the lowercase company name
    (`synqra` → `ADD0 1032`). All-zero here = **internal** (framework/infrastructure, no external
    company); a non-zero hash = an external company.
  - **`8`** — RFC 9562 **version**, fixed at `8` (v8 = `1000`). This nibble is *not* free; it is the
    version field and must stay `8`.
  - **`prs`** — **project** + **space** (group-3's 3 low nibbles). `project = 0` = the company's main
    affairs / core project (e.g. Synqra itself); `space` sub-partitions within a project. Both `0` for the
    default/internal project+space (so group-3 reads `8000`). The **project/space boundary is intentionally
    left unspecified** — how these nibbles divide between project and space is a *company-wide* allocation:
    within its own company-hash space each company splits them as it needs (more projects, or more space per
    project) and owns the responsibility of avoiding its own collisions. A company that outgrows the whole
    region simply takes a new company hash.
  - **`v` (env / variant nibble)** — RFC **variant**: its top 2 bits are fixed at `10`, so it ranges
    `8`/`9`/`a`/`b`. Its **2 free low bits are the environment / id-origin mode** (this is where the `10xx`
    freedom lives — the *variant*, never the version):
    - **`8`** = **prod / manual** — a real production id, or a manually-authored well-known id.
    - **`9`** = **test / unittest** — a **hardcoded** test guid, hand-written and pinned (predictable).
    - **`A`** = **test auto-incremented** — minted by the test guid generator during a run
      (`TestGuids.NewAuto()` → `C0DE0000-0000-8000-A000-{n}`, a process-wide monotonic counter).
    - **`B`** = reserved.
  - **`CCC`** — 12-bit **class** (up to 4096). Read it as **category + specifier**: the *first* nibble is
    the **category**, the remaining two are a counter within it. Category `0` means the id names an
    **instance**; any other category means it names a **type**. See the class-code registry below.
  - trailing bytes — the **node**. It is what tells the two apart: an **all-zero node means the id is a
    type**; a non-zero node means it identifies one well-known **instance**. So `…-8005-…0001` is
    stream #1 (an instance) while `…-8F05-000000000000` is a model type — same `05`, different category
    nibble, different meaning.
  - **Neither shape is ever minted at runtime.** In production an instance id is plain **v7** and a
    `[SynqraModel]` type with no explicit id gets a **v5** (SHA-1) hash under `SynqraTypeNamespaceId`
    (`TypeMetadataProvider`) — neither is a `C0DE` value. Hand-written `C0DE` ids are therefore only ever
    well-known constants and test fixtures.
- **Fixed test guids stay RFC-valid** — never the all-zero `00000000-0000-0000-0000-…` (version 0, not
  a legal UUID). Use the internal-test well-known form `C0DE0000-0000-8000-9CCC-…` (`C0DE` magic
  prefix, zero company-hash `0000-0000` = internal, group-3 `8000` = **version 8 / project 0**,
  variant nibble `9` = **test**): `…-9001-…0003` = a class `001` **Component** instance, `…-900C-…` commands,
  `…-9005-…` containers/stream ids. A **type** flips the category nibble away from `0` and zeroes the node —
  e.g. `…-9F01-000000000000` is a model type, distinct from the `…-9001-…0003` component instances it types.
  (Prod flips the variant nibble to `8`: `…-8F01-…` etc.)
- **Because events are `Derive(CommandId, ordinal)`** (CommandId + a small ordinal in the low bytes,
  same class as the command — see the event-id bullet above), a command's derived events live in the
  command's own id space. So **space command ids by `0x100`** in fixtures
  (`…-800C-…000100`, `…-800C-…000200`) to reserve the low byte for their events: the wrapper
  `CommandCreatedEvent` is ordinal 0 (`…000100`), domain events are `…000101`, `…000102`, … There is
  no separate `800E` event class for these instance ids — a derived event inherits its command's class.
  The all-zero **instance** tail (`…-000000000000`) is reserved.
- **Retired:** the default/root **stream** reservation — a stream id is mandatory and has no default.
  The **MasterId** scheme (term/sequence/collection ordering) is not used — there is no master
  election or monotonic cluster clock.
- **`SynqraTypeNamespaceId`** — the object-type namespace, itself a class `000` **singleton** (node `1`):
  `C0DEADD0-1032-8000-8000-000000000001`. It is the fixed salt fed to `CreateVersion5(namespace,
  type.FullName)` for any `[SynqraModel]` type that has no explicit id. It is a persisted contract
  (derived type ids are written into stored events), so once data exists it must not change. It was
  migrated once from the legacy random salt `BAD8F923-FA74-4CA0-9AA3-70BB874ACC76`; consumer types
  that were persisted under the old salt carry `[SynqraLegacyTypeId(oldId, when, why)]` aliases so
  their existing events still resolve.

### Class codes (`CCC`) (registry)

`CCC` is **category + specifier**. The first nibble is the category; it decides whether the id names an
instance or a type, and the node confirms it (non-zero = instance, all-zero = type). Keep both tables in
sync when reserving a code.

**Category `0` — instance classes** (node is a non-zero counter):

| `CCC` | class | node / instance tail | note |
|---|---|---|---|
| `000` | **singleton** | counter of singletons | a well-known one-off value — neither an instance of a model type nor a type. e.g. `SynqraTypeNamespaceId` = `…-8000-000000000001` (node `1`). All-zero node reserved. |
| `001` | **component** | instance counter | entity / component instances |
| `002` | collection | instance counter | **retired** — do not re-allocate; existing fixtures still use it |
| `003` | link | instance counter | **retired** — links fold into components; do not re-allocate |
| `005` | container / **stream** | instance counter | moved here from `00C` when that code was reassigned to command |
| `00C` | **command** | instance counter, **spaced by `0x100`** | the low byte is reserved for the command's derived events, which inherit its class |
| `00E` | — | — | **deliberately never allocated**: a derived event's instance id lives in its command's class, so an event *instance* class must not exist |

**Categories `A`/`C`/`E`/`F` — type codes** (node **all-zero**, mandatory):

| category | holds | members |
|---|---|---|
| `C` | **command** types | `8C00` `Command` (base) · `8C0F` `SingleObjectCommand` (shared base) · `8C01`–`8C03` concretes |
| `E` | **event** types | `8E00` `Event` (base) · `8E0E` `CommandCreatedEvent` · `8E0F` `SingleObjectEvent` (shared base) · `8E01`–`8E03` concretes |
| `A` | **envelopes / messages** | `8A00` `Item` (storage envelope) · `9A01`–`9A05` `TransportOperation` + wire messages |
| `3` | **link** types | `9300` `Link` (base) — retiring with the link vocabulary |
| `F` | **domain models** — anything that is neither a command nor an event | `8F01`+ (consumer model types) |

The category is a **semantic grouping, not an inheritance root**: `F` members share only an interface (and
not all of them), and `A` holds two unrelated envelope roots. Where a category *does* have an abstract base
type, that base takes specifier `00`, intermediate shared bases take `0E`/`0F`, and concrete types take
`01`+. `F` is the highest nibble on purpose — it keeps a type id visually clear of the `0` instance
classes (`8005` = stream instance vs `8F05` = a model type).

> **Known exception.** `C0DE0000-0000-8000-9040-…` / `-9041-…` (`SynqraModelAttributeTests`) are *type*
> ids sitting in category `0` under an older flat-numbering scheme. They predate the category split and
> are the only ids that violate the rule above.

### Reserved built-in type ids (registry)

Every built-in Synqra type carries an explicit `[SynqraModel("C0DEADD0-1032-8000-<g4>-000000000000")]`:
company hash `ADD0 1032` = `SHA256('synqra')[:4B]`, group-3 `8000` = v8 + default project/space, node
`000000000000` = the type itself. Group-4 `<g4>` = `<env><category><nn>` — category `C` command · `E` event ·
`3` link · `A` envelope/message *(provisional home)*; `nn` = type number (`00` = the category's base type,
`0E`/`0F` = abstract shared bases, `01`+ = concretes).
**A command and the event it emits share `nn`** (e.g. `8C01` `AddComponentCommand` → `8E01`
`ComponentAddedEvent`). Keep this table in sync when adding a built-in type.

> **Note — the `env` column below overloads the variant nibble.** Per the variant spec above, `9` means
> *hardcoded-test*. This table instead uses `9` for two other things: **dying** (`9C0x`, `9E0x`, `9300` —
> the retiring object/link vocabulary) and **provisional** (`9A0x` — production transport types parked in
> test space). Three meanings for one nibble; see the open question at the end of this section.

| g4 | type | env | note |
|---|---|---|---|
| `8C00` | `Command` | live | command base |
| `8C0F` | `SingleObjectCommand` | live | abstract shared base |
| `8C01` | `AddComponentCommand` | live | ↔ `8E01` |
| `8C02` | `ChangeComponentPropertyCommand` | live | ↔ `8E02` |
| `8C03` | `DeleteComponentCommand` | live | ↔ `8E03` |
| `9C01` | `ChangeObjectPropertyCommand` | dying | ↔ `9E01` |
| `9C02` | `DeleteObjectCommand` | dying | (no paired event) |
| `9C03` | `AddLinkCommand` | dying | ↔ `9E03` |
| `9C04` | `RemoveLinkCommand` | dying | ↔ `9E04` |
| `8E00` | `Event` | live | event base |
| `8E0F` | `SingleObjectEvent` | live | abstract shared base |
| `8E0E` | `CommandCreatedEvent` | live | framework wrapper (not command-correlated) |
| `8E01` | `ComponentAddedEvent` | live | ↔ `8C01` |
| `8E02` | `ComponentPropertyChangedEvent` | live | ↔ `8C02` |
| `8E03` | `ComponentDeletedEvent` | live | ↔ `8C03` |
| `9E01` | `ObjectPropertyChangedEvent` | dying | ↔ `9C01` |
| `9E03` | `LinkAddedEvent` | dying | ↔ `9C03` |
| `9E04` | `LinkRemovedEvent` | dying | ↔ `9C04` |
| `9300` | `Link` | dying | link base |
| `8A00` | `Item` | live | File-store envelope — **provisional category A** |
| `9A01` | `TransportOperation` | prov | **provisional category A** |
| `9A02` | `EventEnvelope` | prov | carries one event either direction — **provisional category A** |
| `9A03` | `SubscribeRequest` | prov | client → master, refusable — **provisional category A** |
| `9A04` | `UnsubscribeRequest` | prov | client → master, refusable — **provisional category A** |
| `9A05` | `SubscriptionState` | prov | master → client, authoritative set — **provisional category A** |

> **Open question — promote the `9A0x` transport ids.** `TransportOperation` and the four wire messages
> are *production* types living in the `9` (test) variant, self-labelled `PROVISIONAL placement` in
> `TransportOperations.cs`. They should move to the `8` variant. `8A00` is already taken by `Item`, so
> either they take `8A01`–`8A05` alongside it, or envelopes split into two categories (storage vs wire).
> Changing a `[SynqraModel]` id after data exists needs `[SynqraLegacyTypeId]` — but these are ephemeral
> transport messages whose peers deploy together, so the window to move them cheaply is now.

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
