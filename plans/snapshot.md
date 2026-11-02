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
broadcasts. `SnapshotBegin` → chunks → `SnapshotEnd` → backlog go through the socket's own outbox (`ReplicationOutbox`) with
its pushes held, so no broadcast interleaves; the pushes queued meanwhile follow in commit order. The global `broadcastGate` is
held only to register the socket, seed, cut and read the watermarks, never while anything is sent: a slow client holds up only
its own pushes.

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

### 5. Client apply (as built)

- `EventReplicationService` stays transport-only. A fully cold client (no `ServerWatermark`, nothing in its log) that knows
  its stream (`EventReplicationConfig.StreamId` or `InitialSubscribeStreamId`) sends kind 4; `RequestSnapshot` (on by default)
  turns it off.
- `SnapshotBegin`: the header is checked at once (`SnapshotSchema.CreateReader`). A refused header ends the connection; the
  next one, for the rest of that run, asks for the ordinary replay. `SnapshotChunk`s are collected in order; one out of place
  ends the connection.
- `SnapshotEnd`: the counts are checked, then the whole snapshot is appended to the client's own log as **one record**, a
  `StreamSnapshotEvent` (header + chunks as received, SBX id -88). Only then does `ServerWatermark` become `W` and get
  persisted, and `EventsReceived` is raised. A connection lost before that leaves the log empty and the watermark unset: the
  next connection asks again.
- The projection folds that record like any event of the log: `InMemoryProjection.VisitAsync(StreamSnapshotEvent)` decodes
  the chunks with the header's reader and applies each record as an in-memory `ComponentAddedEvent` (`IsReplay = true`; same
  materialization, link index and uniqueness checks as replay), roots first, each entity at its master version. Its cursor
  then moves past the record, and the backlog after `W` follows through the keeper as usual.
- The send loop never offers the record, and the endpoint refuses one if a client sends it anyway.
- An event the master stamped at or before `W` is never applied again: a live push of an event committed before the cut can
  arrive after the newer backlog (found by the concurrent-writes test). At startup a log that begins with a snapshot sets `W`
  as the floor of the resume point too, so a client that keeps no replication state (Contoso's browser) is not sent the
  history to fold over its snapshot.
- `SnapshotDeclined`, before or after a `SnapshotBegin`: whatever part came is dropped, and the backlog that follows is the
  whole stream.

### 6. Phase 2 — warm loads, compaction

Warm loads are covered by §5: the snapshot is the first record of the client's log, so every load folds it and then the events
after it, and the header kept with it is what makes a months-old snapshot readable. What remains of phase 2 is **compaction**:
the client rewrites its snapshot from its in-memory state (same writer, §3) when the log past it grows beyond a threshold, and
only then truncates the log below the new one — the "compaction horizon" of docs/model.md. A stored snapshot whose header a
later build refuses must be dropped and fetched again (today the fold steps over it and logs an error). Tracked in TODO.md.

### 7. Follow-ups and open points

- Done: per-socket send queues. The snapshot and the backlog are sent outside the global `broadcastGate` (§1).
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
4. DONE — `IStreamSnapshotSource`, `SnapshotRecord`/`SnapshotCut`, `SnapshotChunkWriter`/`Reader` (CommonStoreSupport, where
   `SnapshotSchema` moved); the `MongoProjection` cut (§4, registered by `AddMongoProjection`) and the `InMemoryProjection` one
   (`AddInMemoryStreamSnapshots`).
5. DONE — Endpoint: kind 4 sends the snapshot, then the backlog after its watermark; declines with the reason otherwise.
6. DONE — Client (§5): cold detection, header check, the snapshot kept as a `StreamSnapshotEvent` in the local log and folded
   by `InMemoryProjection`; the watermark floor and the covered-event rule.
7. DONE — Tests: `StreamSnapshotSyncronizationTests` against an in-memory and a Mongo-backed master (snapshot client ==
   replaying client == master, writes both ways after it, warm reload with and without a state store, writes during the cut,
   a cut that breaks off, a refused header); Contoso browser tests (`SceneReplicationBrowserTests`) against a Mongo-backed
   Contoso master: a browser creates a scene, a fresh browser loads it on its first load from a snapshot, both together.
8. Quotaly: pin bump, prove on pr.quotaly.com, then phase 2.
