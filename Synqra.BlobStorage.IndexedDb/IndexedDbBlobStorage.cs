using System.Diagnostics;
using System.Runtime.CompilerServices;
using Synqra;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.IndexedDb;

// Backs one Synqra stream when pinned. The IndexedDB database name carries the stream id, so a
// different user re-logging into the same browser reads/writes a physically separate database and
// never mixes with the previous user's acknowledged event copy. Which stream this instance serves
// is fixed at construction by an optional StreamPin (a security boundary — see StreamPin): a pin
// means single-tenant, physically isolated storage (database name {db}-{streamId:N}); no pin means
// the multitenant bare database (every stream in one database, told apart by Event.StreamId above
// this layer), exactly like the durable File/Mongo event stores. Instance-per-stream when pinned:
// to switch streams on re-login, dispose this (which closes its database) and create the next
// stream's storage.
internal class IndexedDbBlobStorage<TKey> : ITailAppendBlobStorage<TKey>, IJsonMirrorBlobStorage<TKey>, IClearableBlobStorage, IPagedBlobReader<TKey>, IUniqueTagIndex
	where TKey : notnull, IComparable<TKey>
{
	private readonly IndexedDbJsInterop _indexedDbInterop;
	private readonly string _storeName;
	private readonly Func<TKey, string> _getKeyFromItem;
	private readonly Func<string, TKey> _getKeyFromText;
	private readonly IndexedDbBlobStorageOptions _options;
	private readonly string _databaseName;
	// The stream a pinned instance serves is known at construction, so only opening the database is
	// deferred: the storage graph is built early (host start), but the first read/write happens once
	// the app is interactive. See InitializeAsync.
	private readonly Lazy<Task> _initTask;

	public bool WantsJsonMirror => _options.PopulateDebugJson;

	public IndexedDbBlobStorage(
		  IndexedDbJsInterop jsInterop
		, string storeName
		, Func<TKey, string> getKeyFromItem
		, Func<string, TKey> getKeyFromText
		, IndexedDbBlobStorageOptions options
		, StreamPin? streamPin
		)
	{
		_indexedDbInterop = jsInterop;
		_storeName = storeName;
		_getKeyFromItem = getKeyFromItem;
		_getKeyFromText = getKeyFromText;
		_options = options;
		_databaseName = ComposeDatabaseName(options.DatabaseName, streamPin);
		_initTask = new Lazy<Task>(() => AsyncInvoker.InvokeAsync(InitializeAsync()));
	}

	// A stream_id is a security boundary — the database name that physically isolates one stream's
	// records from another's derives directly from it. No pin keeps the bare base name (the
	// multitenant store, whose records are told apart by Event.StreamId one layer up).
	internal static string ComposeDatabaseName(string baseName, StreamPin? streamPin)
		=> streamPin is null ? baseName : $"{baseName}-{streamPin.StreamId:N}";

	private async Task InitializeAsync()
	{
		await _indexedDbInterop.InitializeAsync(_databaseName, _options.ObjectStoreName);
	}

	public async ValueTask<byte[]> ReadBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		var blob = await _indexedDbInterop.GetBlobAsync(_databaseName, _options.ObjectStoreName, _storeName, _getKeyFromItem(key));
		if (blob is null)
		{
			throw new KeyNotFoundException("Blob is not found for key " + key);
		}

		return blob;
	}

	public async ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		await _indexedDbInterop.AddBlobAsync(_databaseName, _options.ObjectStoreName, _storeName, _getKeyFromItem(key), blob, json: null);
	}

	public async ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, string json, CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		await _indexedDbInterop.AddBlobAsync(_databaseName, _options.ObjectStoreName, _storeName, _getKeyFromItem(key), blob, json);
	}

	public async ValueTask<TKey> ReadTailAsync(CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		var tail = await _indexedDbInterop.GetTailKeyAsync(_databaseName, _options.ObjectStoreName, _storeName);
		return tail is null ? default! : _getKeyFromText(tail);
	}

	// Every tab of the browser shares this database: the tag check, the tail check and the write run in one IndexedDB
	// transaction (see appendAtTail), so tabs agree on one order and one copy without talking to each other.
	public async ValueTask<TailAppendResult> AppendAtTailAsync(TKey key, ReadOnlyMemory<byte> blob, string? json, string uniqueTag, TKey expectedTail, CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		var expectedTailText = Equals(expectedTail, default(TKey)) ? null : _getKeyFromItem(expectedTail);
		var outcome = await _indexedDbInterop.AppendAtTailAsync(_databaseName, _options.ObjectStoreName, _storeName, _getKeyFromItem(key), blob, json, uniqueTag, expectedTailText);
		return outcome switch
		{
			"appended" => TailAppendResult.Appended,
			"duplicate" => TailAppendResult.Duplicate,
			"tailMoved" => TailAppendResult.TailMoved,
			_ => throw new InvalidOperationException($"IndexedDB appendAtTail returned an unknown outcome '{outcome}'."),
		};
	}

	public async ValueTask DeleteBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		await _indexedDbInterop.DeleteAsync(_databaseName, _options.ObjectStoreName, _storeName, _getKeyFromItem(key));
	}

	public async IAsyncEnumerable<TKey> EnumerateKeysAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		var currentFrom = from is null || Equals(from, default(TKey))
			? null
			: _getKeyFromItem(from);
		var fromExclusive = true; // a cursor names a key the caller holds; every later page continues after its last key the same way
		const int pageSize = 1024;

		while (true)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				yield break;
			}

			var page = (await _indexedDbInterop.GetKeysAsync(_databaseName, _options.ObjectStoreName, _storeName, currentFrom, fromExclusive, pageSize)).ToArray();
			if (page.Length == 0)
			{
				yield break;
			}

			foreach (var keyText in page)
			{
				yield return _getKeyFromText(keyText);
			}

			currentFrom = page[^1];
			fromExclusive = true;
		}
	}

	public async IAsyncEnumerable<(TKey Key, byte[] Blob)> EnumerateBlobsAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		var currentFrom = from is null || Equals(from, default(TKey))
			? null
			: _getKeyFromItem(from);
		const int pageSize = 512;
		var more = true;
		var pages = 0;
		var records = 0;
		long bytes = 0;
		long waited = 0;
		while (more && !cancellationToken.IsCancellationRequested)
		{
			var asked = Stopwatch.GetTimestamp();
			var packed = await _indexedDbInterop.GetBlobsPageAsync(_databaseName, _options.ObjectStoreName, _storeName, currentFrom, fromExclusive: currentFrom is not null, pageSize);
			waited += Stopwatch.GetTimestamp() - asked;
			pages++;
			bytes += packed.Length;
			var count = 0;
			foreach (var (keyText, blob) in Unpack(packed))
			{
				count++;
				currentFrom = keyText;
				yield return (_getKeyFromText(keyText), blob);
			}
			records += count;
			more = count == pageSize;
		}
		if (EmergencyLog.Default.IsStaging())
		{
			EmergencyLog.Default.Stage($"indexeddb {_storeName}: {records} records, {pages} pages, {bytes / 1024} KB, waited {Stopwatch.GetElapsedTime(0, waited).TotalMilliseconds:F0} ms");
		}
	}

	/// <summary>The records of one getBlobsPage buffer.</summary>
	IEnumerable<(string Key, byte[] Blob)> Unpack(byte[] packed)
	{
		var at = 0;
		while (at < packed.Length)
		{
			var keyLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(at, 4));
			var key = System.Text.Encoding.UTF8.GetString(packed, at + 4, keyLength);
			at += 4 + keyLength;
			var blobLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(at, 4));
			var blob = packed.AsSpan(at + 4, blobLength).ToArray();
			at += 4 + blobLength;
			yield return (key, blob);
		}
	}

	/// <summary>The store's unique tags, a page of keys per transaction; the module keeps them under "{store}#tag".</summary>
	public async IAsyncEnumerable<string> EnumerateTagsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		const int pageSize = 4096;
		var tagStore = _storeName + "#tag";
		string? currentFrom = null;
		var more = true;
		while (more && !cancellationToken.IsCancellationRequested)
		{
			var page = await _indexedDbInterop.GetKeyPageAsync(_databaseName, _options.ObjectStoreName, tagStore, currentFrom, currentFrom is not null, pageSize);
			foreach (var tag in page)
			{
				yield return tag;
			}
			more = page.Length == pageSize;
			if (page.Length > 0)
			{
				currentFrom = page[^1];
			}
		}
	}

	// Wipes every record for this store within this stream's database only (the store name
	// partitions blob kinds within the per-stream database). Used by resync recovery.
	public async Task ClearAllAsync(CancellationToken cancellationToken = default)
	{
		await _initTask.Value;
		await _indexedDbInterop.ClearStoreAsync(_databaseName, _options.ObjectStoreName, _storeName);
	}

	public void Dispose()
	{
	}

	// Closing the stream's database is the graceful-stop half of a re-login switch: release this
	// stream's connection before the next stream's storage opens its own database. Sibling stores
	// sharing the same stream (same database, different object store) are not a concern today —
	// only the Event append log uses this — so a close here is a clean per-stream teardown.
	public async ValueTask DisposeAsync()
	{
		if (!_initTask.IsValueCreated)
		{
			// Never used → no database was ever opened, so there is nothing to close.
			return;
		}

		try
		{
			await _initTask.Value;
		}
		catch
		{
			// A database that never finished opening has no connection to close.
			return;
		}

		await _indexedDbInterop.CloseDatabaseAsync(_databaseName);
	}
}
