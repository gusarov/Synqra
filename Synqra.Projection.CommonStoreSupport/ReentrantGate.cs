namespace Synqra;

/// <summary>
/// One writer at a time, re-entrant within the holder's own async flow: a callback that runs inside a
/// write (EventsCommitted, Activate, a veto) and submits synchronously re-enters instead of deadlocking.
/// The hold ends with the gated work; a task the holder started that outlives it does not inherit it.
/// </summary>
internal sealed class ReentrantGate
{
	readonly SemaphoreSlim _gate = new(1, 1);
	readonly AsyncLocal<Hold?> _held = new();

	sealed class Hold
	{
		public bool Active { get; set; } = true;
	}

	public async Task<T> RunAsync<T>(Func<Task<T>> work)
	{
		T result;
		if (_held.Value is { Active: true })
		{
			result = await work();
		}
		else
		{
			await _gate.WaitAsync();
			var hold = new Hold();
			try
			{
				_held.Value = hold;
				result = await work();
			}
			finally
			{
				hold.Active = false;
				_held.Value = null;
				_gate.Release();
			}
		}
		return result;
	}

	public Task RunAsync(Func<Task> work) => RunAsync(async () =>
	{
		await work();
		return 0; // nothing to return
	});

	public void Run(Action work)
	{
		if (_held.Value is { Active: true })
		{
			work();
		}
		else
		{
			_gate.Wait();
			var hold = new Hold();
			try
			{
				_held.Value = hold;
				work();
			}
			finally
			{
				hold.Active = false;
				_held.Value = null;
				_gate.Release();
			}
		}
	}
}
