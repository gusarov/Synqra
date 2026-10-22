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

## Replication / AppendStorage

- [ ] **Batched tail append for IndexedDB.** `EventReplicationService` now hands a received batch to the
  local log as one `AppendBatchAsync`, but `BlobEventStorage` over an `ITailAppendBlobStorage` (the
  browser's IndexedDB) still appends it one event at a time: a `ReadTailAsync` plus an
  `AppendAtTailAsync` transaction per event. A batched tail append (one IndexedDB transaction for the
  whole run, each event still deduplicated by its `EventId` tag) would cut a catching-up browser's
  per-event cost further.
- [ ] **JSON-lines append is O(file size).** `Synqra.AppendStorage.JsonLines/JsonLinesStorage.cs` →
  `AppendAsync` (the `#define LOCK` path) reads the whole file byte by byte on every append to blank an
  earlier record with the same key, so appending N events is quadratic. A test replaying thousands of
  events through it measures that scan, not replication (see
  `BacklogReplaySyncronizationTests.Should_replay_a_long_backlog_to_a_fresh_client_in_batches`, which
  swaps in the in-memory log for that reason). Keep a key → offset index, or drop the replace path for
  logs that never rewrite a key.
