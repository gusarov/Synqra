using Microsoft.JSInterop;

namespace Contoso.Wasm.OpfsPoc;

public sealed class OpfsPocJsInterop : IAsyncDisposable
{
	private readonly Lazy<Task<IJSObjectReference>> _moduleTask;

	public OpfsPocJsInterop(IJSRuntime jsRuntime)
	{
		_moduleTask = new Lazy<Task<IJSObjectReference>>(() => jsRuntime
			.InvokeAsync<IJSObjectReference>("import", "./opfsPoc.js")
			.AsTask());
	}

	public async Task<OpfsPocResult> RunAsync()
	{
		var module = await _moduleTask.Value;
		return await module.InvokeAsync<OpfsPocResult>("runOpfsPoc");
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
