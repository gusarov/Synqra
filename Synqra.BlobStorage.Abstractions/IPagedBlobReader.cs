namespace Synqra.BlobStorage;

/// <summary>
/// A blob storage that reads keys and blobs together, a page per round trip. Without it a full read is one
/// <see cref="IBlobStorage{TKey}.ReadBlobAsync"/> per key, which for IndexedDB is one transaction per record.
/// </summary>
public interface IPagedBlobReader<TKey>
	where TKey : notnull, IComparable<TKey>
{
	/// <summary>Every record after <paramref name="from"/> (all of them when it is default), in key order.</summary>
	IAsyncEnumerable<(TKey Key, byte[] Blob)> EnumerateBlobsAsync(TKey? from = default, CancellationToken cancellationToken = default);
}
