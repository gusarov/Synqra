using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Synqra.AppendStorage;

#pragma warning disable CS8424 // The EnumeratorCancellationAttribute will have no effect. I know, but I want a tooling to auto-insert this attribute.

/// <summary>
/// Low-level storage interface for storing and retrieving events
/// </summary>
public interface IAppendStorage<T, TKey> : IDisposable, IAsyncDisposable
	// where T : IIdentifiable<TKey>
{
	Task<string> TestAsync(string input);

	Task AppendAsync(T item, CancellationToken cancellationToken = default);
	Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default);

	IAsyncEnumerable<T> GetAllAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default);

	[EditorBrowsable(EditorBrowsableState.Advanced)]
	Task FlushAsync(CancellationToken cancellationToken = default)
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
		=> Task.CompletedTask
#endif
		;
}
