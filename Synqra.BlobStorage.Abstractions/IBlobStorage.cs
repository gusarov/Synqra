using System.Runtime.CompilerServices;

namespace Synqra.BlobStorage;

#pragma warning disable CS8424 // The EnumeratorCancellationAttribute will have no effect. It is still useful for tooling.

public interface IBlobStorage<TKey> : IDisposable, IAsyncDisposable
	where TKey : notnull, IComparable<TKey>
{
	/// <summary>
	/// Every key in key order. A full <paramref name="from"/> key is a cursor: the read starts strictly after it;
	/// default reads everything. A composite key with an empty tail starts at the first key under that prefix; only
	/// some storages (File) stop at the prefix's end, the others run on into later prefixes.
	/// </summary>
	IAsyncEnumerable<TKey> EnumerateKeysAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default);

	ValueTask<byte[]> ReadBlobAsync(TKey key, CancellationToken cancellationToken = default);
	ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default);
	ValueTask DeleteBlobAsync(TKey key, CancellationToken cancellationToken = default);

	bool SupportsSyncOperations
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
		=> false
#endif
		;

	void WriteBlob(TKey key, ReadOnlySpan<byte> blob)
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
		=> throw new NotSupportedException($"Synchronous blob writes are not supported by {GetType().Name}.")
#endif
		;

	void DeleteBlob(TKey key)
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
		=> throw new NotSupportedException($"Synchronous blob deletes are not supported by {GetType().Name}.")
#endif
		;
}

/// <summary>
/// Optional capability — implemented only by backends where "wipe everything and resync
/// fresh from the server" is a meaningful, safe recovery action (currently just IndexedDb,
/// a disposable client-local cache). File/Sqlite/Mongo backends are the durable source of
/// truth themselves, so they don't implement this; check with an `is` pattern match rather
/// than adding it to <see cref="IBlobStorage{TKey}"/> itself.
/// </summary>
public interface IClearableBlobStorage
{
	Task ClearAllAsync(CancellationToken cancellationToken = default);
}
