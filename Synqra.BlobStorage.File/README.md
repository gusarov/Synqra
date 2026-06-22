# Synqra.BlobStorage.File

File-system implementation of `IBlobStorage<TKey>` from `Synqra.BlobStorage.Abstractions`.

Each blob is stored as a **separate file** on disk. There are no external dependencies — only the standard .NET file APIs.

Targets `net8.0`, `net9.0`, and `net10.0`.

---

## How it works

### One blob = one file

Every `WriteBlobAsync` / `WriteBlob` call maps the key to a file path and writes the raw bytes directly to disk using `File.WriteAllBytes`. There is no database, no index file, and no container format — the storage directory is a plain directory tree you can inspect, copy, or back up with any standard tool.

### Key-to-path mapping

Keys are serialized to hex strings by the caller-supplied `getPathFromKey` delegate and then split into a shallow directory hierarchy by `GetFileNameForRec`. The hierarchy strategy depends on the key type:

| Key kind | Prefix length | Example path |
|---|---|---|
| UUIDv7 (time-sorted) | 6 chars | `550e84/00e29b41d4a716446655440000` |
| UUIDv6 | 5 chars | `550e8/400e29b41d4a716446655440000` |
| Other GUID | 3 chars | `550/e8400e29b41d4a716446655440000` |
| Non-GUID / short key | 2 chars | `55/0e8400e29b` |

Composite keys (two concatenated GUIDs, e.g. `(Guid, Guid)`) produce a **two-level path**: the first GUID is mapped to a directory, and the second GUID becomes a nested path inside it.

This prefix-bucketing keeps individual directories small, which matters for file-systems that degrade on large flat directories, while staying **human-readable and diff-friendly**.

### Folder layout

The root folder is resolved from `FileBlobStorageOptions.Folder` (default: `storage/[Store]`).  
The `[Store]` token is replaced at runtime with the store name (typically the entity type name):

```
storage/
  MyEntity/           ← store name
    550e84/
      00e29b41d4a716446655440000   ← blob file for one key
    7f3a01/
      ...
```

If the configured path does not contain `[Store]`, the store name is appended as a sub-directory automatically.

### Lazy initialization

The storage directory is created on the **first write**, not at construction. Read and enumeration on a non-existent directory return "not found" or an empty sequence without throwing.

### Synchronous support

`SupportsSyncOperations` returns `true`. Both sync (`WriteBlob`, `DeleteBlob`) and async variants are fully implemented, making this storage suitable in contexts where async I/O is not available or desired (e.g., initial data loading, tests).

### Key enumeration

`EnumerateKeysAsync` walks the directory tree recursively in **ordinal, depth-first order** (files before subdirectories at each level). An optional `from` parameter filters to keys whose hex prefix matches — useful for range scans over time-sorted UUIDv7 keys.

---

## Comparison with other storages

| Feature | **File** | **Sqlite** | **MongoDb** | **IndexedDb** |
|---|---|---|---|---|
| Backend | OS filesystem | Single SQLite DB file | MongoDB server | Browser IndexedDB (JS) |
| External dependency | None | `Microsoft.Data.Sqlite` | MongoDB driver | JS interop |
| Sync operations | ✅ | ✅ | — | ❌ |
| Human-inspectable | ✅ (plain files) | ❌ (binary DB) | ❌ | ❌ |
| Suitable for server | ✅ | ✅ | ✅ | ❌ |
| Suitable for WASM/browser | ❌ | ❌ | ❌ | ✅ |
| Transactional writes | ❌ | ✅ (WAL) | ✅ | — |
| Range enumeration | Prefix-based | SQL `>=` | — (not implemented) | — |

> **MongoDb** blob storage is a placeholder and not yet implemented.

---

## Registration (Dependency Injection)

```csharp
// Guid key — most common case
builder.AddBlobStorageFile<MyEntity>(e => e.Id);

// Composite (Guid, Guid) key
builder.AddBlobStorageFile<MyEntity>(e => (e.TenantId, e.Id));

// Custom key type with explicit converters
builder.AddBlobStorageFile<MyEntity, MyKey>(
    e => e.Key,
    key => key.ToHexString(),
    hex => MyKey.FromHex(hex));
```

### Configuration

Bind via `appsettings.json` under `Storage:BlobStorage:File`:

```json
{
  "Storage": {
    "BlobStorage": {
      "File": {
        "Folder": "data/blobs/[Store]"
      }
    }
  }
}
```

The `[Store]` placeholder is replaced with the store name at runtime. Omitting `[Store]` appends the store name as a sub-directory automatically.
