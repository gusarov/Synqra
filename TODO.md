# Synqra TODO

Deferred / known-improvement items. Prefer referencing an entry here over leaving a silent
`// TODO` in code, so the work is discoverable and tracked.

## Projection.MongoDb

- [ ] **`MongoStoreCollection<T>` enumerator should stream, not materialize.**
  `Synqra.Projection.MongoDb/MongoStoreCollection.cs` → `IEnumerable<T>.GetEnumerator()` calls
  `_mongo.Find(Scope).ToList()`, which eagerly loads the entire (scoped) collection into memory
  before yielding anything. Switch to lazy cursor streaming (e.g. `Find(Scope).ToEnumerable()`, or
  explicit `ToCursor()` iteration) so `yield return` pulls documents on demand and large
  collections aren't fully materialized. Mind cursor lifetime/disposal across the `yield`.

## Replication / stream snapshots (plans/snapshot.md)

- [ ] **Client log compaction.** A client's log starts with the snapshot it loaded (`StreamSnapshotEvent`) and then grows
  with every event after it. Rewrite the snapshot from the in-memory state (`SnapshotChunkWriter`, the same record format)
  once the log past it grows beyond a threshold, append it, and only then truncate the log below it; the client's IndexedDB
  log then stays proportional to state, not history (plans/snapshot.md §6).
- [ ] **Server log retention.** Nothing truncates the master's Mongo log. Snapshots make a cold client independent of
  history, so a retention policy (keep N days, or everything after the oldest watermark a client still resumes from) is now
  possible; decide it with the undo/audit needs of the command log.
- [ ] **A stored snapshot a later build refuses.** `InMemoryProjection` steps over a `StreamSnapshotEvent` whose header it
  cannot read (logged as an error), and the projection then lacks that state. Drop the local log and its replication state
  instead, and fetch a fresh snapshot.
- [ ] **Per-socket send queues.** The endpoint sends a snapshot, like a backlog, under the global `broadcastGate`: every
  other connection's broadcasts wait while one cold client's snapshot is written. Give each socket its own send queue.
- [ ] **Missing events in a browser log.** A prod browser log held 12,928 of the master's 14,594 events after a full
  catch-up (plans/snapshot.md §7). A snapshot load bypasses the cause; it is still unknown.
- [ ] **Header names every mapping.** `SnapshotSchema.DescribeSnapshot` lists the writer's full map, so a client lacking
  any one mapped type (even one the stream never uses) refuses the whole snapshot and replays. Collect the ids the chunks
  actually wrote instead, if that becomes a deployment problem.
- [ ] **Mongo reads racing a delete.** `MongoProjection` drops a deleted entity from its tracking after the commit, so a reader
  that enumerated the entity a moment earlier gets "The object is not attached to this MongoProjection" from `GetId`. Found by
  the Contoso master listing a scene while a browser's delete landed (CI build 14977); the scene readers skip such a node. A
  store-level answer (`TryGetId`, or ids handed out with the enumeration) would spare every caller that.
- [ ] **A Mongo collection's `ToList` is two queries.** `List<T>`/`ToList` over a `MongoStoreCollection` size their array by
  `Count` (one query) and fill it by `CopyTo` (another). `CopyTo` now stops at the array's end when a root was added in between,
  but a root deleted in between leaves the list's last slot null, and `ICollection<T>`'s two calls give `CopyTo` no way to
  shrink it. Callers that must not see a torn read enumerate once (`ContosoScene`). A store-level answer: collections that
  do not expose `Count` and `CopyTo` as separate reads, or a `Count` served from the same read.
