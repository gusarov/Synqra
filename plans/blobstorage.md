## Plan: Blob Layer Split for AppendStorage

Introduce a new blob-oriented storage layer between append semantics and engine specifics. Binary engines (File, IndexedDb, Sqlite) will implement blob contracts only, while BlobAppendStorage will own SBX serialization and IAppendStorage behavior. Keep JsonLines unchanged. Add ordered key enumeration now and shape blob APIs to support stack-based writes where possible without breaking async-only engines.

**Steps**
1. Phase 1 - Define blob contracts in Synqra.AppendStorage.Abstractions (foundation).
2. Create IBlobStorage<TKey> as the required async contract with ordered keys:
3. Add EnumerateKeysAsync(TKey? from = default, CancellationToken ct = default) returning IAsyncEnumerable<TKey>.
4. Add ReadBlobAsync(TKey key, CancellationToken ct = default) returning ValueTask<byte[]> for now (lowest migration risk).
5. Add WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, CancellationToken ct = default).
6. Add DeleteBlobAsync(TKey key, CancellationToken ct = default).
7. Constrain TKey as notnull, IComparable<TKey>.
8. Add optional sync fast-path members directly on IBlobStorage<TKey>: WriteBlob(TKey key, ReadOnlySpan<byte> blob), DeleteBlob(TKey key).
9. Rationale for split: IndexedDb is async-only (JS interop), so sync methods cannot be mandatory on the base interface.
10. Phase 2 - Add BlobAppendStorage adapter in Synqra (depends on 1).
11. Create BlobAppendStorage<T, TKey> implementing IAppendStorage<T, TKey> and composing IBlobStorage<TKey> + ISbxSerializerFactory + Func<T,TKey>.
12. AppendAsync/AppendBatchAsync: serialize to stack span for small payloads, then call WriteBlobAsync(ReadOnlyMemory<byte>).
13. If underlying storage supports sync operations, use sync WriteBlob(ReadOnlySpan<byte>) fast-path for small chunks; fallback to async WriteBlobAsync otherwise.
14. GetAsync: ReadBlobAsync then deserialize.
15. GetAllAsync(from): EnumerateKeysAsync(from) then ReadBlobAsync for each key and deserialize.
16. FlushAsync: keep no-op unless a concrete blob storage exposes flush behavior.
17. Keep ID behavior unchanged: direct key mapping as it works today.
18. Phase 3 - Refactor File engine to blob-only (depends on 1, parallel with 4 and 5).
19. Create FileBlobStorage<TKey> in Synqra.AppendStorage.File and move path mapping and file IO there.
20. Implement IBlobStorage<TKey> with sync overrides (file engine can benefit most from span sync path).
21. Move GetFileNameFor/GetFileNameForRec and ordering-preserving path logic from current FileAppendStorage.
22. Leave any object-tracking cache decisions to BlobAppendStorage or defer if risky for first migration.
23. Update DI in FileAppendStorageExtensions to register FileBlobStorage + BlobAppendStorage for IAppendStorage.
24. Phase 4 - Refactor Sqlite engine to blob-only (depends on 1, parallel with 3 and 5).
25. Create SqliteBlobStorage<TKey> in Synqra.AppendStorage.Sqlite.
26. Move table setup, ordered key encoding, insert/read/delete SQL logic here.
27. Implement IBlobStorage<TKey>; override sync members where the sync API is naturally available.
28. Preserve existing big-endian Guid ordering behavior for stable sort and range scans.
29. Update DI in SqliteAppendStorageExtensions to register SqliteBlobStorage + BlobAppendStorage.
30. Phase 5 - Refactor IndexedDb engine to blob-only (depends on 1, parallel with 3 and 4).
31. Create IndexedDbBlobStorage<TKey> in Synqra.AppendStorage.IndexedDb.
32. Move JS interop add/get/list/delete logic there and map records to raw blobs.
33. Implement IBlobStorage<TKey> only (do not force sync interface here).
34. Update DI in IndexedDbAppendStorageExtensions (file _DI..cs) to register IndexedDbBlobStorage + BlobAppendStorage.
35. Phase 6 - Keep custom JsonLines storage untouched (independent).
36. Do not route JsonLines through IBlobStorage in this change.
37. Phase 7 - Verification and compatibility checks (depends on 2-6).
38. Build solution and run append-storage related tests.
39. Validate behavior parity: same IDs, same read ordering, same data retrieval semantics.
40. Validate GetAllAsync(from) correctness via EnumerateKeysAsync across File/Sqlite/IndexedDb.
41. Validate no regression in current serialization behavior (SBX payloads still round-trip).

**Relevant files**
- Synqra.AppendStorage.Abstractions/IAppendStorage.cs - existing append contract to keep stable.
- Synqra.AppendStorage.Abstractions/Synqra.AppendStorage.Abstractions.csproj - multi-target constraints for new interfaces.
- Synqra.AppendStorage.File/FileAppendStorage.cs - source of file path and IO logic to extract.
- Synqra.AppendStorage.File/FileAppendStorageExtensions.cs - DI rewiring.
- Synqra.AppendStorage.Sqlite/SqliteAppendStorage.cs - SQL and ordering logic to extract.
- Synqra.AppendStorage.Sqlite/SqliteAppendStorageExtensions.cs - DI rewiring.
- Synqra.AppendStorage.IndexedDb/IndexedDbAppendStorage.cs - blob operations to extract.
- Synqra.AppendStorage.IndexedDb/IndexedDbJsInterop.cs - async-only constraints and paging behavior.
- Synqra.AppendStorage.IndexedDb/_DI..cs - DI rewiring for browser storage.

**Verification**
1. Compile check after Phase 1: all projects referencing append abstractions build for their target frameworks.
2. Contract check: BlobAppendStorage compiles against IBlobStorage only, with optional runtime use of sync members.
3. File engine check: append/get/getAll parity with prior behavior and path layout.
4. Sqlite check: key ordering and range semantics preserved.
5. IndexedDb check: async operations and paging still function, no sync requirement introduced.
6. End-to-end check: existing append-storage tests pass, plus targeted tests for EnumerateKeysAsync from-key behavior.

**Decisions**
- EnumerateKeysAsync is included in IBlobStorage now.
- JsonLines remains direct IAppendStorage and is out of this refactor.
- DeleteBlob exists now for future compaction workflows; compaction itself is out of scope.
- Recommended API shape: required async Memory contract + optional sync write Span contract via separate interface.

**Further Considerations**
1. Read-side allocation strategy in Phase 1:
Option A: keep ReadBlobAsync returning byte[] first for fastest migration, then evolve to pooled ownership later.
Option B: introduce pooled ownership now (IMemoryOwner<byte>) for lower allocations but higher complexity.
Recommendation: Option A for this refactor, Option B in a follow-up optimization pass.
2. Caching policy location:
Option A: move weak-reference cache into BlobAppendStorage for shared behavior.
Option B: keep cache only in File path first to minimize risk.
Recommendation: start with Option B for minimal regression risk, then unify in a separate step.
