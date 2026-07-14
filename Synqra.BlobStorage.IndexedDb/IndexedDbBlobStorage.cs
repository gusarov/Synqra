using System.Runtime.CompilerServices;
using Synqra;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.IndexedDb;

internal class IndexedDbBlobStorage<TKey> : IBlobStorage<TKey>, IJsonMirrorBlobStorage<TKey>
	where TKey : notnull, IComparable<TKey>
{
	private readonly IndexedDbJsInterop _indexedDbInterop;
	private readonly string _storeName;
	private readonly Func<TKey, string> _getKeyFromItem;
	private readonly Func<string, TKey> _getKeyFromText;
	private readonly bool _wantsJsonMirror;
	private readonly Task _initTask;

	public bool WantsJsonMirror => _wantsJsonMirror;

	public IndexedDbBlobStorage(
		  IndexedDbJsInterop jsInterop
		, string storeName
		, Func<TKey, string> getKeyFromItem
		, Func<string, TKey> getKeyFromText
		, bool wantsJsonMirror
		)
	{
		_indexedDbInterop = jsInterop;
		_storeName = storeName;
		_getKeyFromItem = getKeyFromItem;
		_getKeyFromText = getKeyFromText;
		_wantsJsonMirror = wantsJsonMirror;
		_initTask = AsyncInvoker.InvokeAsync(_indexedDbInterop.InitializeAsync());
	}

	public async ValueTask<byte[]> ReadBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		await _initTask;
		var blob = await _indexedDbInterop.GetBlobAsync(_storeName, _getKeyFromItem(key));
		if (blob is null)
		{
			throw new KeyNotFoundException("Blob is not found for key " + key);
		}

		return blob;
	}

	public async ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
	{
		await _initTask;
		await _indexedDbInterop.AddBlobAsync(_storeName, _getKeyFromItem(key), blob, json: null);
	}

	public async ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, string json, CancellationToken cancellationToken = default)
	{
		await _initTask;
		await _indexedDbInterop.AddBlobAsync(_storeName, _getKeyFromItem(key), blob, json);
	}

	public async ValueTask DeleteBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		await _initTask;
		await _indexedDbInterop.DeleteAsync(_storeName, _getKeyFromItem(key));
	}

	public async IAsyncEnumerable<TKey> EnumerateKeysAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await _initTask;
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

			var page = (await _indexedDbInterop.GetKeysAsync(_storeName, currentFrom, fromExclusive, pageSize)).ToArray();
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

	public void Dispose()
	{
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}
