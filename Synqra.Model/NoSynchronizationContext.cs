namespace Synqra;

/// <summary>
/// A scope with no SynchronizationContext on the current thread, for a caller that blocks on store work
/// (a collection Add, a generated setter): it cannot pump its context while it waits, so a continuation
/// posted back to it would never run (Blazor's renderer during SSR). The context is restored on dispose.
/// </summary>
public readonly struct NoSynchronizationContext : IDisposable
{
	readonly SynchronizationContext? _previous;

	public NoSynchronizationContext()
	{
		_previous = SynchronizationContext.Current;
		SynchronizationContext.SetSynchronizationContext(null);
	}

	public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
}
