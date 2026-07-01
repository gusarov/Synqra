using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Synqra.AppendStorage;

#pragma warning disable CS8424 // The EnumeratorCancellationAttribute will have no effect. I know, but I want a tooling to auto-insert this attribute.

/// <summary>
/// Low-level storage interface for storing and retrieving events
/// </summary>
public interface IAppendStorage<T, TKey> : IDisposable, IAsyncDisposable
	where T : class
	where TKey : notnull
	// where T : IIdentifiable<TKey>
{
	Task AppendAsync(T item, CancellationToken cancellationToken = default);
	async Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
	{
		foreach (var item in items)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (item is null)
			{
				throw new ArgumentException("Items cannot contain null values.", nameof(items));
			}
			await AppendAsync(item, cancellationToken);
		}
	}

	Task<T> GetAsync(TKey key, [EnumeratorCancellation] CancellationToken cancellationToken = default);
	IAsyncEnumerable<T> GetAllAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default);

	[EditorBrowsable(EditorBrowsableState.Advanced)]
	Task FlushAsync(CancellationToken cancellationToken = default)
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
		=> Task.CompletedTask
#endif
		;
}

/// <summary>
/// Optional capability — implemented only where "wipe everything and resync fresh from the
/// server" is a meaningful, safe recovery action (currently just a client's local IndexedDb
/// cache; see <see cref="Synqra.BlobStorage.IClearableBlobStorage"/>). Not part of
/// <see cref="IAppendStorage{T, TKey}"/> itself — File/Sqlite/Mongo-backed storage is the
/// durable source of truth, not a disposable cache, so most implementations shouldn't have to
/// implement this at all; check with an `is` pattern match instead (see
/// EventReplicationService's own use of this).
/// </summary>
public interface IClearableAppendStorage
{
	Task ClearAllAsync(CancellationToken cancellationToken = default);
}
