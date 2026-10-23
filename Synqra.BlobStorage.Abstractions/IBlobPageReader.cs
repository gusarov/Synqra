namespace Synqra.BlobStorage;

/// <summary>
/// Optional capability: blobs read a page at a time, each key with its bytes, in key order strictly after
/// <c>from</c> (from the start when it is default). A store whose every read is a round trip (IndexedDB behind JS
/// interop) serves a whole log this way instead of one read per key; <see cref="IBlobStorage{TKey}.ReadBlobAsync"/>
/// stays for single lookups. Check with an <c>is</c> pattern, like <see cref="IClearableBlobStorage"/>.
/// </summary>
public interface IBlobPageReader<TKey>
	where TKey : notnull, IComparable<TKey>
{
	IAsyncEnumerable<KeyValuePair<TKey, byte[]>> EnumerateBlobsAsync(TKey? from = default, CancellationToken cancellationToken = default);
}
