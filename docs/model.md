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

## 8. Ids

### 8.1 The three mechanisms at a glance

Synqra mints exactly three shapes of GUID. Which one applies is decided by *what the id names*, never
by the call site's convenience.

| shape | version | used for | minted by | readable? |
|---|---|---|---|---|
| **opaque instance** | **v7** | every runtime instance — entities, components, commands, events | `ISynqraIdProvider` (production) | no — time-ordered only |
| **derived type id** | **v5** | a `[SynqraModel]` type with no explicit id | `TypeMetadataProvider` (SHA-1 over `type.FullName`) | no |
| **structured id** | **v8** `C0DE…` | well-known constants, and test fixtures | hand-written, or `DeterministicSynqraIdProvider` under test | **yes** — carries an allocation mode + a semantic class |

A structured id is the only shape with meaning inside it. The other two are opaque by design and
callers MUST NOT try to read structure out of them — use `Guid.IsStructuredId()` before reading a
mode or a class.

### 8.2 v7 — instance ids

- **v7 GUIDs for all data** (entities, components, links) — monotonic and totally ordered, which the
  undirected `min` folding relies on.
- Production instance ids come from `ISynqraIdProvider` (`CreateComponentId`, `CreateStreamId`,
  `CreateCommandId`, …). Call sites MUST take the provider from DI; there is no ambient static
  factory. `SynqraIdProvider.Default` exists only for the rare non-DI construction path.
- Command ids are minted **spaced by `0x100`**, reserving the low node byte for the events that
  command expands into (§8.9).

### 8.3 v5 — derived type ids

A `[SynqraModel]` type that declares no explicit id gets `CreateVersion5(SynqraTypeNamespaceId,
type.FullName)` — a SHA-1 hash, opaque and high-entropy.

- **`SynqraTypeNamespaceId`** = `C0DEADD0-1032-8000-8000-000000000001` (family `0` code `00`, node
  `1`). It is the fixed salt, and a **persisted contract**: derived type ids are written into stored
  events as the `_t` discriminator, so once data exists it MUST NOT change.
- It was migrated once from the legacy random salt `BAD8F923-FA74-4CA0-9AA3-70BB874ACC76`. Consumer
  types persisted under the old salt carry `[SynqraLegacyTypeId(oldId, when, why)]` aliases so their
  existing events still resolve.
- Because a derived id is unreadable and appears in *every* event of that type, authors SHOULD give a
  type an explicit structured id instead (§8.8). Built-in Synqra types all do.

### 8.4 v8 `C0DE…` — the structured layout

Authoritative source: `Synqra.Model/SynqraGuids.cs`.

This subsection and §8.5–§8.7 describe **CODE v8** itself — a portable convention that any company can
adopt. What the individual families and codes *mean* is a per-adopter profile; Synqra's own profile
starts at §8.8.

```
C0DE yyyy-yyyy 8prs  m F nn  iiiiiiiiiiii
│    │         │     │ │ │   └── node                (48 bits)
│    │         │     │ │ └────── semantic code       (8 bits)  ┐ together the
│    │         │     │ └──────── semantic family     (4 bits)  ┘ 12-bit semantic class
│    │         │     └────────── allocation mode     (4 bits)
│    │         └──────────────── version 8 + project + space
│    └────────────────────────── company hash
└─────────────────────────────── magic prefix
```

- **`C0DE`** — magic prefix (hex-readable "CODE"); marks a structured id at a glance. A v8 GUID
  *without* this prefix is an unrelated hash and MUST NOT be parsed as structured.
- **`yyyy-yyyy`** — company hash: first 4 bytes of SHA-256 of the lowercase company name
  (`synqra` → `ADD0 1032`, `quotaly` → `7F1D 6199`). All-zero = **internal** (framework
  infrastructure and Synqra's own test fixtures, no external company).
- **`8`** — RFC 9562 **version**, fixed at `8`. This nibble is the version field and MUST stay `8`.
- **`prs`** — **project** + **space**. Both `0` for the default project and space, so group-3 reads
  `8000`. The project/space boundary is deliberately unspecified: within its own company hash each
  company splits these three nibbles as it needs and owns avoiding its own collisions. A company that
  outgrows the region takes a new company hash.
- **`m` (allocation mode)** — the RFC **variant** nibble. Its top 2 bits are fixed at `10`, so it
  ranges `8`/`9`/`A`/`B`; the two free low bits are two independent dimensions (§8.5). This is the only
  place in the id where the RFC leaves bits free — never the version.
- **`F nn` (semantic class)** — 12 bits: a **family** nibble plus a **family-local code** (§8.6).
- **`iiii…` (node)** — 48 bits. Its zero-ness is what separates a type from an instance (§8.7).

**Read group-4 as `m` `F` `nn`.** `8005` is mode `8`, family `0`, code `05` — *not* "family 5". Low
values like `001` and `005` are codes inside family `0`, never families of their own.

**Neither a structured instance nor a structured type id is ever minted at runtime in production** —
production instances are v7 and production type ids are v5 or hand-written constants. Structured ids
are therefore only ever well-known constants, test fixtures, and `DeterministicSynqraIdProvider`
output.

### 8.5 Allocation mode `m` — a 2×2 matrix, not a flat enum

RFC 9562 fixes the variant nibble's top bit and leaves **two free bits**. Those two bits encode two
**orthogonal** dimensions — never a four-value enum:

|  | pinned / manual | generated |
|---|---|---|
| **committed** registry | `8` | `A` |
| **staging** registry | `9` | `B` |

```
8 = 1000    9 = 1001    A = 1010    B = 1011
       ^^          ^^          ^^          ^^
       ││          ││          ││          ││
       │└─ staging bit (registry)
       └── generated bit (provenance)
```

- **registry bit** — which **semantic registry** the family-local code was allocated in. The committed
  and staging registries are **independent**: `8C02` and `9C02` share a class value but need not
  describe the same thing.
- **generated bit** — **provenance only**: was this id pinned by hand in a registry, or minted by a
  provider? It never changes semantic identity.

The two invariants that follow, and the one line to remember:

> **The generated bit preserves semantic identity; the staging bit changes the registry.**

- `8C02 ↔ AC02` and `9C17 ↔ BC17` are the *same* semantic allocation seen as a pinned type and as a
  generated instance. `A` is the generated form of `8`; `B` is the generated form of `9`.
- `9 → 8` is **promotion**, not a bit flip: a fresh allocation in the other registry, which may land on
  a different local code (§8.10).

| mode | `AllocationMode` | meaning |
|---|---|---|
| `8` | `CommittedPinned` | a firm, normative constant; changing it breaks persisted data |
| `9` | `StagingPinned` | hand-pinned but still mutable — fixtures, designs in flight |
| `A` | `CommittedGenerated` | minted by a provider, projecting a committed allocation |
| `B` | `StagingGenerated` | minted by a provider, projecting a staging allocation |

In code the raw nibble is the `[Flags] AllocationMode` enum (`Variant | Staging | Generated`), paired
with bit-level helpers so nothing has to pretend `8`/`9`/`A`/`B` are unrelated values:
`Guid.GetAllocationMode()`, `.IsCommitted()`, `.IsStaging()`, `.IsGenerated()`, `.IsPinned()`.

### 8.6 Semantic class `Fnn`

**`Fnn` is the 12-bit semantic class carried by a structured id.**

```
F nn
│ └── code allocated inside that family and registry (8 bits)
└──── semantic family                                (4 bits)
```

Semantic interpretation is therefore **`registry + Fnn`** — the mode's registry bit selects which
registry the code was allocated in, while the generated bit contributes nothing to meaning. Helpers:
`Guid.GetSemanticClass()` (12 bits), `.GetSemanticFamily()`, `.GetSemanticCode()`.

Worked examples (families are Synqra-profile, §8.8):

| id group-4 | mode | family | code | reads as |
|---|---|---|---|---|
| `8005` | committed + pinned | `0` default | `05` | the well-known stream constant |
| `9A15` | staging + pinned | `A` message | `15` | a staging transport type |
| `AA04` | committed + generated | `A` message | `04` | a generated instance of committed message type `04` |
| `BE15` | staging + generated | `E` event | `15` | a generated instance of staging event type `15` |
| `AC02` | committed + generated | `C` command | `02` | a generated instance of committed command type `02` |

### 8.7 Type ids vs instance ids

The **node** is the discriminator, and it is normative:

| node | the id names | example |
|---|---|---|
| **all-zero** | a **type** | `…-8C01-000000000000` — a command type |
| **non-zero** | one **instance** | `…-8005-000000000001` — stream #1 |

- A type id MUST have an all-zero node. An instance id MUST NOT.
- The semantic class inside an **instance** id is a **human-readability hint only**. Type resolution
  always goes through an explicit type-id field (`TargetTypeId`, `ComponentTypeId`, the `_t`
  discriminator) — never through an instance id. This matters because the registries are independent,
  so the same class value can appear in both for two different types; that ambiguity is harmless
  precisely because nothing resolves a type from an instance id.

---

*Everything below is the **Synqra profile** of CODE v8: what the families and codes mean here.*

### 8.8 Synqra profile — semantic families

The family nibble is a **semantic grouping, not an inheritance root**. Where a family does have an
abstract base type, that base takes local code `00`, intermediate shared bases take `0E`/`0F`, and
concretes take `01`+.

| family | holds |
|---|---|
| `0` | **default / unqualified** — standalone well-known values and plain domain models |
| `A` | **envelopes / messages** — storage and wire envelopes |
| `C` | **commands** |
| `E` | **events** |

There is no generic-model family: a domain model type lives in family `0`. (An earlier draft used `F`
for that; it is dropped, and all `F` allocations were migrated into family `0`.)

Family `0` is a single code space shared by well-known values, core instance kinds and model types —
the node (§8.7) says which of those an id is:

| code | names | note |
|---|---|---|
| `00` | **no semantic type** — a standalone well-known value | e.g. `SynqraTypeNamespaceId` (node `1`) |
| `01` | component / entity | instance counter |
| `02` | collection | **retired** — do not re-allocate |
| `03` | link | **retired** — links fold into components |
| `05` | container / **stream** | instance counter |
| `0C` | — | **never allocate.** `00C` is family `0` code `0C`, not "a command"; commands are family `C` |
| `40`+ | staging-registry test model types | |
| `80`–`FF` | synthesised by `DeterministicSynqraIdProvider` for a type that has no allocated code | visibly "unallocated" |

Instances in the other families: a **command** instance node is spaced by `0x100`, reserving its low
byte for the events it expands into; an **event** instance node is always *derived* from its command
(§8.9), never allocated independently.

### 8.9 Synqra profile — event-id derivation

Events are **derived, not random**: each event a command expands to gets
`GuidExtensions.DeriveEventId(commandId, eventTypeId, ordinal)`. This makes the command→event
expansion reproducible across nodes and replays (core.md §8) with no clock and no shared counter.

- **Opaque command id (production, v7).** There is nowhere to put a semantic class, so derivation is
  exactly `Derive(commandId, ordinal)` — the command's v7 with its low bytes incremented. The derived
  id stays a time-ordered v7 sorting adjacent to its command, and `Derive(cmd, 0) == cmd`. **Production
  event ids are bit-for-bit unchanged by the structured rules below.**
- **Structured command id (`C0DE` v8).** The result is composed from three sources:

  | field | taken from |
  |---|---|
  | company hash, project/space, base node | the **command instance** |
  | registry bit, semantic family, semantic code | the **event type** |
  | generated bit | the **derivation** itself |

  So a committed event type `8E03` yields `AE03`, and a staging event type `9E15` yields `BE15` —
  never `9E03`, and never the command's own `Cnn`. The command instance's own mode is deliberately
  **not** consulted: a staging-pinned command still derives a committed-generated event from a
  committed event type.
- Only the **48-bit node** advances by `ordinal`, so a carry can never reach the mode or the class.
  Node overflow is an error, not a wrap.
- Consequently `DeriveEventId(cmd, evType, 0) != cmd` for a structured id — the ordinal-0 wrapper names
  its own event type (`8E0E` `CommandCreatedEvent`) instead of aliasing the command.
- A structured command whose **event type has no structured id** is an **error**, not a fallback:
  emitting the command's own `Cnn` as the event's class would be a false semantic claim.

Because the node advances and the class is replaced, fixtures MUST space command ids by `0x100`
(`…-AC01-…000100`, `…-AC01-…000200`) so a command's events fill its low byte without colliding with
the next command: `…-AE0E-…000100` (wrapper, ordinal 0), `…-AE01-…000101`, `…-AE01-…000102`, …

Under test, `DeterministicSynqraIdProvider` mints generated ids with a per-class counter, keeping the
registry of the type being instantiated: a committed type yields `A…`, a staging type `B…`. It never
mints a family-less generic id.

### 8.10 Promotion between registries

A staging type that settles is **re-allocated** into the committed registry. Promotion changes the
registry bit *and may change the local code*, because the two registries number independently:

```
before:                        after:
  9A15  pinned staging type      8A04  pinned committed type
  BA15  generated instance       AA04  generated instance
```

- `9A15 → 8A04` — promotion. There is no entitlement to `8A15`; the committed allocator picks the next
  free committed code.
- `8A04 ↔ AA04` and `9A15 ↔ BA15` — the same allocation, pinned vs generated. These pairings are
  guaranteed; the promotion arrow is not.
- Retired committed allocations are not re-used.

### 8.11 Reserved built-in type ids (registry)

Every built-in Synqra type carries an explicit `[SynqraModel("C0DEADD0-1032-8000-<g4>-000000000000")]`:
company hash `ADD0 1032` = `SHA256('synqra')[:4B]`, group-3 `8000` = v8 + default project/space, node
all-zero = the type itself. Group-4 `<g4>` = `<mode><family><nn>`.
**A command and the event it emits share `nn`** (e.g. `8C01` `AddComponentCommand` → `8E01`
`ComponentAddedEvent`). Keep this table in sync when adding a built-in type.

| g4 | type | state | note |
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
| `8E0E` | `CommandCreatedEvent` | live | framework wrapper, ordinal 0 of every command |
| `8E01` | `ComponentAddedEvent` | live | ↔ `8C01` |
| `8E02` | `ComponentPropertyChangedEvent` | live | ↔ `8C02` |
| `8E03` | `ComponentDeletedEvent` | live | ↔ `8C03` |
| `9E01` | `ObjectPropertyChangedEvent` | dying | ↔ `9C01` |
| `9E03` | `LinkAddedEvent` | dying | ↔ `9C03` |
| `9E04` | `LinkRemovedEvent` | dying | ↔ `9C04` |
| `9300` | `Link` | dying | link base |
| `8A00` | `Item` | live | File-store envelope |
| `9A01` | `TransportOperation` | staging | wire base |
| `9A02` | `EventEnvelope` | staging | carries one event either direction |
| `9A03` | `SubscribeRequest` | staging | client → master, refusable |
| `9A04` | `UnsubscribeRequest` | staging | client → master, refusable |
| `9A05` | `SubscriptionState` | staging | master → client, authoritative set |

> **Open question — promote the `9A0x` transport ids.** Family `A` is their permanent home; only the
> registry bit is unsettled. They are production types still sitting in **staging**. Moving them to the
> committed registry is cheap only while no long-lived data carries them — these are ephemeral
> transport messages whose peers deploy together, so the window is now. Per §8.10 they would need fresh
> committed local codes (`8A00` is `Item`); a promotion does not carry the number across.

> **Open question — the `9Cxx`/`9Exx` "dying" built-ins.** `ObjectPropertyChangedEvent`, `LinkAddedEvent`
> and `LinkRemovedEvent` are shipped, persisted types sitting in the *staging* registry, which is now
> load-bearing: their generated instances derive as `B…` rather than `A…`. They are on the way out with
> the object/link vocabulary, so they are left alone; re-allocating them into the committed registry
> would change persisted identity and needs `[SynqraLegacyTypeId]` aliases.

### 8.12 Legacy and retired

- **Retired: the default/root stream reservation.** A stream id is mandatory and has no default.
- **Retired: the MasterId scheme** (term/sequence/collection ordering). There is no master election
  and no monotonic cluster clock.
- **Retired: family `F`** as the generic-model bucket. Domain models live in family `0`; the former
  `F` allocations were migrated, and `F` MUST NOT be re-introduced as a family.
- **Retired family-`0` codes `02` (collection) and `03` (link).** Existing fixtures still carry them;
  they MUST NOT be re-allocated.
- **`00C` is not a command.** It is family `0` code `0C` — a generic "some command" instance class that
  briefly existed and MUST NOT return. Commands are family `C`, and a command instance carries the
  `Cnn` of the concrete command type it is.
- **`E` is never a family for independently-allocated instance ids.** An event instance id is always
  derived from its command (§8.9).
- **Changing a type's id** after data exists requires keeping the previous id as
  `[SynqraLegacyTypeId(oldId, when, why)]` so persisted events still resolve. A brand-new type has no
  history and MUST NOT be given one.
- **Known gap:** `ObjectDeletedEvent` (namespace exactly `Synqra`) carries no `[SynqraModel]` id at
  all, so it falls back to a v5 derived id. It escapes the built-in guard because that guard tests
  `Namespace.StartsWith("Synqra.")` — with the dot. Assigning it an id now would change persisted
  identity, so it is left as-is and recorded here.
- **This revision is the baseline.** Committed (`8`) allocations are firm from here; staging (`9`) is
  the working registry; `A`/`B` are their generated projections. No compatibility aliases were added
  for earlier drafts of this convention.

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
