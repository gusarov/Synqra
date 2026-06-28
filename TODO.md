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

## CodeGeneration / ObjectData

- [ ] **Generate native `ObjectData`-shaped read+write on `[SynqraModel]` types instead of
  reflecting at runtime.** `ObjectData.From(object source, ...)` (`Synqra.Model/ObjectData.cs`) and
  `ObjectDataApplyHelpers.HydrateFromData` (`Synqra.Projection.CommonStoreSupport/ComponentAndLinkApplyHelpers.cs`)
  both fall back to `Type.GetProperties()`/reflection for the read and write directions
  respectively. For `[SynqraModel]` types specifically this is unnecessary: `ModelBindingGenerator`
  already walks the same partial-property set (with ancestors) at compile time for `SetCore`/the
  generated setters — it could just as easily emit a native
  `IEnumerable<KeyValuePair<string, object?>>` (read direction, feeds `ObjectData.From`'s fast path)
  and reuse the existing `IBindableModel.Set` codegen (write direction, already reflection-free) so
  the reflection branches in both helpers become a rare fallback for plain POCOs/anonymous objects
  instead of the only path. Matters most for AOT/trimming (the codebase already tracks
  `DynamicallyAccessedMembers`/IL2075 warnings elsewhere) — a generated dictionary view sidesteps
  that entirely for the common case. Scoped as its own generator feature, not a quick patch: needs a
  decision on minimal (`IEnumerable<KeyValuePair<>>`) vs. full `IReadOnlyDictionary<string, object?>`
  surface, and a way to exclude well-known fields (e.g. `Link.LinkId`/`SourceId`/`TargetId`) from the
  generated view — possibly an attribute on the property — instead of the manual `exclude` parameter
  callers pass to `ObjectData.From` today.
