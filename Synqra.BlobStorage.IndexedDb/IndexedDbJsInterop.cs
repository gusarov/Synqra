using Microsoft.JSInterop;
using Microsoft.Extensions.Options;

namespace Synqra.BlobStorage.IndexedDb;

public class IndexedDbJsInterop : IAsyncDisposable
{
	private readonly IndexedDbBlobStorageOptions _options;
	private readonly Lazy<Task<IJSObjectReference>> _moduleTask;

	public IndexedDbJsInterop(IJSRuntime jsRuntime, IOptions<IndexedDbBlobStorageOptions> options)
	{
		_options = options.Value;
		_moduleTask = new Lazy<Task<IJSObjectReference>>(() => jsRuntime
			.InvokeAsync<IJSObjectReference>("import", "./_content/Synqra.BlobStorage.IndexedDb/indexedDbJsInterop.js")
			.AsTask());
	}

	public async Task<string> TestAsync(string message)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<string>("test", message);
	}

	public async Task InitializeAsync()
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("initialize", _options.DatabaseName, _options.ObjectStoreName);
	}

	public async Task AddBlobAsync(string storeName, string keyText, ReadOnlyMemory<byte> blob)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("addBlob", storeName, keyText, blob.ToArray());
	}

	public async Task<byte[]?> GetBlobAsync(string storeName, string keyText)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<byte[]?>("getBlob", storeName, keyText);
	}

	public async Task<IEnumerable<string>> GetKeysAsync(string storeName, string? fromKeyText = default, bool fromExclusive = false, int pageSize = 1024)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<IEnumerable<string>>("getKeys", storeName, fromKeyText, fromExclusive, pageSize);
	}

	public async Task DeleteAsync(string storeName, string keyText)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("deleteByKey", storeName, keyText);
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
