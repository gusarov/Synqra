## Plan: Stream snapshot — a cold client loads state, not history

A cold client today replays the stream's whole log: the master sends every event as its own `EventEnvelope`, the
client appends each to IndexedDB, then folds all of them into a fresh `InMemoryProjection`. Cost grows with history,
not with state. A snapshot ships the live entities instead, as of one watermark, then the log resumes after it.

**Measured** (Quotaly prod stream `dacee3ae…`, 2026-10-27, real `SbxSerializer` with Scene's map):

| | bytes |
|---|---|
| Log: 14,594 events, SBX | ~3.7 MB (256 B/event) |
| State: 624 objects, 4,777 entities (roots + components + links), minimal record | **301 KB** (63 B/entity) |
| Same, typed data alone | 149 KB — the other half is two raw 16-byte ids per entity (ids are v5, not v7) |
| Same, as one `ComponentAddedEvent` per entity | 685 KB — event wrapper ids double it; a snapshot never uses the event shape |
| Largest single entity | 787 B |

At a 64 KB chunk budget the state is ~5 chunks.

### 1. Protocol

**Negotiation.** A new HELLO kind, `ReplicationHelloKind.SubscribeToSnapshot = 4`, 41 bytes, same layout as
`SubscribeTo`. The client sends it only when it is fully cold: `ServerWatermark == Guid.Empty` **and** the local log holds
no events (`logHoldsEvents == false`). Anything else is the existing `SubscribeTo`. A master that cannot serve one (no
`IStreamSnapshotSource` for the stream, or the stream is empty) answers `SnapshotDeclined` and continues with the
ordinary backlog replay, so the client always gets an explicit answer. A pre-snapshot master refuses kind 4 (unknown kind);
the Quotaly server and its WASM client ship together, and an old cached client keeps sending kind 3 (`SubscribeTo`), which the new master
still serves — server-first is the only order that must work, and it does.

**Frames** (all `TransportOperation`, family A, next free staging ids `9A08`…`9A0B`; pinned, mode 9):

| Frame | Fields | Notes |
|---|---|---|
| `SnapshotBegin` | `FormatVersion long`, `StreamId Guid`, `SnapshotId Guid` (v7), `Watermark Guid`, `TimeBase long` (Unix seconds), `Types IList<SnapshotType>`, `EntityCount long` (expected) | the **schema header** (§2) |
| `SnapshotChunk` | `SnapshotId Guid`, `Index long`, `Count long`, `Payload byte[]` | `Count` records, SBX, decoded with the header's map (§3) |
| `SnapshotEnd` | `SnapshotId Guid`, `Watermark Guid`, `ChunkCount long`, `RecordCount long` | the commit point on the client |
| `SnapshotDeclined` | `StreamId Guid`, `Reason string` | the client falls back to replay, which is already flowing |

After `SnapshotEnd` the master continues exactly like a warm resume: backlog strictly after `Watermark`, then live
broadcasts. `SnapshotBegin` → chunks → `SnapshotEnd` → backlog are sent under the same `broadcastGate` the backlog replay
holds today, so no broadcast interleaves (see §7 for why that gate is a follow-up).

### 2. Schema header — every type the payload uses, pinned

SBX writes no schema version: `Map(id, type)` fixes the version at the reader's own latest `[Schema]`, and the generated
binder reads with that. A reader on another build — or a snapshot persisted months ago — would silently decode the wrong
layout. The header removes the implicit agreement:

```
SnapshotType { MapId long, TypeId Guid, Version double, Schema string }
```

- One entry per SBX map id that occurs in the payload — every root, component and link type, and any mapped type nested in
  their data. The writer collects them while it writes the chunks (it knows each id it emitted); `SnapshotBegin` is sent
  before the chunks, so the writer either uses the host's full map (simple, ~30 entries, ~2 KB) or buffers the first
  chunk. v1: the full map of the stream's registered types.
- `MapId ↔ TypeId` binds the payload's numbering to Synqra type ids, so a payload never depends on the reader's own
  numbering (Scene's 300-block is host-private).
- `Version` + `Schema` (the `[Schema]` signature string, e.g. `"1 SourceId Guid TargetId Guid"`): the version alone is not
  exact, because a version may be amended in place with an appended nullable field (BinarySerializer readme). The reader
  accepts an entry only when it has that exact `(Version, Schema)` among the type's `[Schema]` attributes.
- **Reading:** the client builds a fresh serializer from the header — `Map(MapId, Version, ResolveType(TypeId))` per
  entry, `SetTimeBase(TimeBase)` — instead of its default factory. The generated binders already take `schemaVersion` and
  read older layouts, so a header one or two versions back decodes with no new code.
- **Refusal:** an entry with an unknown `TypeId`, or a `(Version, Schema)` the reader does not have, rejects the whole
  snapshot before any chunk is applied. The client drops it, wipes nothing (nothing was written), and reconnects with
  kind 3 (`SubscribeTo`) — the old, slow, always-correct path. Logged once with the offending entry.
- `FormatVersion` (1) versions everything the header cannot describe: the record layout (§3) and the frames themselves.
  The frames' own layouts are fixed by their `[Schema]` and the HELLO kind that negotiated them.

### 3. Record layout (FormatVersion 1)

Each chunk's payload is records back to back, written with **one serializer context per chunk** (`Reset()` at chunk
start), so a chunk decodes alone and can be stored alone:

```
ComponentId   Guid
Owner         Guid?    null = implied: the record itself for a root (ComponentId == entity id); for a link, Data.SourceId
Version       Guid?    roots only: the entity's last event id (_lev); null on facets and links
Data          object   typed, by its header map id
```

Implied owners save 16 B on every root and link (624 + 1,758 here, ~38 KB of 301 KB). Order: all roots first, then
facets and links, each grouped by owner — a facet or link never arrives before the entity it hangs on.

**Chunking:** the writer serializes records into one 64 KB rented buffer and closes the chunk at the first record boundary
past 56 KB; a record is never split, and a record larger than the budget goes alone in a buffer sized for it (still
under `DefaultMaxFrameSize`). Measured today: 5 chunks, ~1,000 entities each.

**SBX addition:** `byte[]` (`ReadOnlyMemory<byte>`) as length varint + raw bytes, its own type id. Additive; needed for
`Payload` and reusable by persistence (§6). Without it a byte list costs a varint per byte.

### 4. The cut — consistent state at one watermark

The Mongo projection commits after the log append (docs/model.md §9), so "read the watermark, then scan" can miss an
event that is in the log but not yet in the projection — and the client would resume after it and never see it. The
snapshot source therefore takes the cut under the stream's own submit gate (`MongoProjection.GateOf(streamId)`), held
only for the instant it takes:

1. Enter the stream's gate. Every write that appended is now committed too.
2. Read `W = ReadWatermarkAsync(stream)`.
3. Start a snapshot session (`ClientSessionOptions.Snapshot = true`) and issue its first read — the first page of the
   scan below. A snapshot session fixes `atClusterTime` at its first read, not at its start, so that read must happen
   inside the gate.
4. Leave the gate. Writes resume; the snapshot read does not see them.
5. Scan `Components` for `_sid == stream`, ordered as in §3, under that snapshot (`_sid` index; one stream, no collection
   scan).

The result is exactly the state at `W`; events after `W` reach the client as the ordinary backlog. Both clusters are
already replica sets (§9). The snapshot window (`minSnapshotHistoryWindowInSeconds`, 300 s default) dwarfs a 300 KB scan.

`InMemoryProjection` also implements `IStreamSnapshotSource` (for in-memory masters and tests): under its submit gate it
copies the entity list and reads the log's `IEventWatermarks` watermark for the stream, then serializes outside the
gate. (Its `Cursor` is the local log's own sequence, not a server watermark, so it is never used for this.)

A projection *behind* its log (a failed final commit, §9) yields a snapshot missing that event — the same gap every
reader of that projection has today; the repair is the projection rebuild, not the snapshot.

### 5. Client apply

- `EventReplicationService` stays transport-only. It hands `SnapshotBegin/Chunk/End` to an `IStreamSnapshotSink`
  (registered by the projection area), keeping the raw `Payload` bytes.
- `InMemoryProjectionProvider` implements the sink. On `SnapshotBegin` it validates the header (§2), creates the stream's
  projection and holds the stream's provider gate, so `GetAsync` waits instead of folding an empty log. Each chunk is
  applied through the existing visit path — a `ComponentAddedEvent` built in memory per record, `IsReplay = true` (no
  activator side effects, same materialization, link index and uniqueness checks as replay) — then the entity's
  `LastEventId = Version`. The provider yields to the UI between chunks and reports progress.
- `SnapshotEnd`: counts checked; the projection's cursor stays at the local log's head (empty); the provider marks the
  stream `ColdLoaded`; `ServerWatermark = Watermark` is persisted **only now**. Then the gate opens and the backlog after
  `W` flows into the local log and through the keeper as usual.
- A connection lost mid-snapshot leaves `ServerWatermark` empty and the local log empty: the next connection asks for a
  snapshot again. The half-built projection is discarded.
- `SnapshotDeclined`, a refused header, or a count mismatch: discard, and let the replay path run (`SubscribeTo` on refusal).
- The local log no longer holds the stream's full history — only what arrives after `W` and the node's own writes. That
  is fine for the projection (it never replays below its snapshot) and for replication (dedup by `EventId`; the master
  never resends below `W`).

### 6. Phase 2 — persisted snapshot (warm loads)

Same format, stored: an IndexedDB `Snapshot` store keyed `(SnapshotId, Index)` holding the chunk payloads as received,
plus a manifest `{ SnapshotId, Watermark, header, localCursorAtCut }`. A warm load decodes the stored chunks with the
stored header (the header is what makes a months-old snapshot readable), then folds only the local log after
`localCursorAtCut`. The client rewrites its own snapshot from its in-memory state (same writer, §3) when the log past the
cut grows beyond a threshold, and only then truncates the log below the new cut — the "compaction horizon" of
docs/model.md. A stored snapshot whose header this build refuses is dropped and re-fetched from the master.

### 7. Follow-ups and open points

- `broadcastGate` is global: the backlog — and now the snapshot — is sent while every other connection's broadcasts wait.
  Per-socket send queues remove that; a 300 KB snapshot over a slow link makes it worth doing soon.
- Uplink: a cold client has no local writes to offer, so snapshot and uplink never meet in v1.
- Reuse the snapshot for a *far behind* warm client (watermark older than N events) — needs the client to discard its
  projection first; later.
- Ids are v5/v4 and cost 16 B each; nothing in this plan depends on changing that.
- Prod finding while measuring: the browser log held 12,928 of the master's 14,594 events after a full catch-up. A snapshot
  load bypasses whatever drops them, but the cause is separate and still open.

**Steps**
1. DONE — SBX: `byte[]` support + tests (Synqra.BinarySerializer). Also fixed `DeserializeNullableGuid`, which read a non-null
   `Guid?` one byte off (no model used `Guid?` until the record layout).
2. DONE — Frames + `SnapshotType` models (SBX ids -83..-87), HELLO kind 4, `SnapshotDeclined`; the endpoint accepts kind 4
   and declines it until a snapshot source exists (Synqra.Model, Synqra, Synqra.Replication.AspNetCore).
3. DONE — `SnapshotSchema`: the header lists every mapping of the writer (system and consumer), the reader is built from it,
   refusal rules + tests for amended schemas, unknown versions and types, and reading an older schema.
4. `IStreamSnapshotSource` + chunk writer; `MongoProjection` cut (§4) + `InMemoryProjection` source.
5. Endpoint: kind 4 path, snapshot before backlog, decline fallback (Synqra.Replication.AspNetCore).
6. Client: `IStreamSnapshotSink`, `InMemoryProjectionProvider` apply + gate + progress, cold detection in `EventReplicationService`.
7. Tests: cold client == replayed client (entity-for-entity), mid-snapshot disconnect, refused header falls back, writes
   during the cut are not lost.
8. Quotaly: pin bump, prove on pr.quotaly.com, then phase 2.
