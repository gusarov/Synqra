using Microsoft.JSInterop;

namespace Synqra.AppendStorage.IndexedDb;

public class IndexedDbJsInterop(IJSRuntime jsRuntime) : IAsyncDisposable
{
	private readonly Lazy<Task<IJSObjectReference>> _moduleTask = new(() => jsRuntime.InvokeAsync<IJSObjectReference>(
			"import", "./_content/Synqra.AppendStorage.IndexedDb/indexedDbJsInterop.js").AsTask());

	public async Task<string> TestAsync(string message)
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<string>("test", message);
	}

	public async Task InitializeAsync()
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("initialize");
	}

	public async Task AddAsync<T, TKey>(T newItem, TKey key)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("add", newItem, key);
	}

	public async Task AddBatchAsync<T>(IEnumerable<T> newItems, string keyField)
	{
		var module = await _moduleTask.Value;
		await module.InvokeVoidAsync("addBatch", newItems, keyField);
	}

	public async Task<IEnumerable<T>> GetAllAsync<T, TKey>(TKey? fromKey = default, int pageSize = 1024)
	{
		var module = await _moduleTask.Value;
		var items = await module.InvokeAsync<IEnumerable<T>>("getAll", fromKey, pageSize);
		Console.WriteLine($"GetAllAsync({fromKey}, {pageSize})");
		Console.WriteLine($"count = {items.Count()}");
		/*
		foreach (var item in items)
		{
			// Console.WriteLine(item);
		}
		*/
		return items;
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