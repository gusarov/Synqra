using System.Runtime.CompilerServices;

namespace Synqra.BlobStorage;

#pragma warning disable CS8424 // The EnumeratorCancellationAttribute will have no effect. It is still useful for tooling.

public interface IBlobStorage<TKey> : IDisposable, IAsyncDisposable
	where TKey : notnull, IComparable<TKey>
{
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
