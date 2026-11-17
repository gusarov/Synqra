using Microsoft.JSInterop;

namespace Synqra.BlobStorage.IndexedDb;

// Thin, stateless wrapper over the JS module. Every call names the database explicitly so a
// single shared interop instance can serve the several per-stream databases the browser opens
// over its lifetime (one per stream — see IndexedDbBlobStorage). The database name already
// encodes the stream id; the object store name partitions blob kinds within a stream.
public class IndexedDbJsInterop : IAsyncDisposable
{
	private readonly Lazy<Task<IJSObjectReference>> _moduleTask;

	public IndexedDbJsInterop(IJSRuntime jsRuntime)
	{
		_moduleTask = new Lazy<Task<IJSObjectReference>>(() => jsRuntime
			.InvokeAsync<IJSObjectReference>("import", "./_content/Synqra.BlobStorage.IndexedDb/indexedDbJsInterop.js?v=tags1") // versioned: the edge caches an unversioned module for hours, and this build calls functions an older one lacks
			.AsTask());
	}

	public async Task InitializeAsync(string databaseName, string objectStoreName)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("initialize", databaseName, objectStoreName);
	}

	public async Task CloseDatabaseAsync(string databaseName)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("closeDatabase", databaseName);
	}

	public async Task AddBlobAsync(string databaseName, string objectStoreName, string storeName, string keyText, ReadOnlyMemory<byte> blob, string? json, uint? tag = null)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("addBlob", databaseName, objectStoreName, storeName, keyText, blob.ToArray(), json, tag);
	}

	/// <summary>The store's last key text, or null when it is empty.</summary>
	public async Task<string?> GetTailKeyAsync(string databaseName, string objectStoreName, string storeName)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<string?>("getTailKey", databaseName, objectStoreName, storeName);
	}

	/// <summary>One transaction: writes the record and its tag only while the tag is new and the tail is <paramref name="expectedTailText"/> (null = empty).</summary>
	public async Task<string> AppendAtTailAsync(string databaseName, string objectStoreName, string storeName, string keyText, ReadOnlyMemory<byte> blob, string? json, string uniqueTag, string? expectedTailText, uint? tag = null)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<string>("appendAtTail", databaseName, objectStoreName, storeName, keyText, blob.ToArray(), json, uniqueTag, expectedTailText, tag);
	}

	/// <summary>A record packed by getTaggedBlob (flag, tag, blob), or null.</summary>
	public async Task<byte[]?> GetTaggedBlobAsync(string databaseName, string objectStoreName, string storeName, string keyText)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<byte[]?>("getTaggedBlob", databaseName, objectStoreName, storeName, keyText);
	}

	/// <summary>One page packed by getTaggedBlobsPage: per record a 4-byte key length, the key's UTF-8, a 4-byte length, the packed record.</summary>
	public async Task<byte[]> GetTaggedBlobsPageAsync(string databaseName, string objectStoreName, string storeName, string? fromKeyText, bool fromExclusive, int pageSize)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<byte[]>("getTaggedBlobsPage", databaseName, objectStoreName, storeName, fromKeyText, fromExclusive, pageSize);
	}

	/// <summary>Stores an entry under the side store name ("{store}#{side}"); the same entry again changes nothing.</summary>
	public async Task PutSideEntryAsync(string databaseName, string objectStoreName, string sideStoreName, string keyText, ReadOnlyMemory<byte> content)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("putSideEntry", databaseName, objectStoreName, sideStoreName, keyText, content.ToArray());
	}

	public async Task<byte[]?> GetBlobAsync(string databaseName, string objectStoreName, string storeName, string keyText)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<byte[]?>("getBlob", databaseName, objectStoreName, storeName, keyText);
	}

	public async Task<IEnumerable<string>> GetKeysAsync(string databaseName, string objectStoreName, string storeName, string? fromKeyText = default, bool fromExclusive = false, int pageSize = 1024)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<IEnumerable<string>>("getKeys", databaseName, objectStoreName, storeName, fromKeyText, fromExclusive, pageSize);
	}

	/// <summary>One page of records packed by getBlobsPage: per record a 4-byte key length, the key's UTF-8, a 4-byte blob length, the blob.</summary>
	public async Task<byte[]> GetBlobsPageAsync(string databaseName, string objectStoreName, string storeName, string? fromKeyText = default, bool fromExclusive = false, int pageSize = 1024)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<byte[]>("getBlobsPage", databaseName, objectStoreName, storeName, fromKeyText, fromExclusive, pageSize);
	}

	/// <summary>One transaction, keys only: up to <paramref name="pageSize"/> key texts of the store after the key.</summary>
	public async Task<string[]> GetKeyPageAsync(string databaseName, string objectStoreName, string storeName, string? fromKeyText, bool fromExclusive, int pageSize)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<string[]>("getKeyPage", databaseName, objectStoreName, storeName, fromKeyText, fromExclusive, pageSize);
	}

	public async Task DeleteAsync(string databaseName, string objectStoreName, string storeName, string keyText)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("deleteByKey", databaseName, objectStoreName, storeName, keyText);
	}

	/// <summary>Wipes every record for this storeName within the named database only — used by resync recovery.</summary>
	public async Task ClearStoreAsync(string databaseName, string objectStoreName, string storeName)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("clearStore", databaseName, objectStoreName, storeName);
	}

	public async ValueTask DisposeAsync()
	{
		if (_moduleTask.IsValueCreated)
		{
			var module = await _moduleTask.Value;
			await module.DisposeAsync();
		}
	}
}
