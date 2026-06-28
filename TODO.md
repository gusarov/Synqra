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
