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
internal class IndexedDbBlobStorage<TKey> : IBlobStorage<TKey>, IJsonMirrorBlobStorage<TKey>, IClearableBlobStorage
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
		var fromExclusive = false;
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
