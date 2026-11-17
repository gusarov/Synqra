using System.Runtime.CompilerServices;

namespace Synqra.BlobStorage;

#pragma warning disable CS8424 // The EnumeratorCancellationAttribute will have no effect. It is still useful for tooling.

/// <summary>
/// A blob store whose records carry a small opaque tag as a field beside their bytes (an IndexedDB field, a column, a document
/// field), and which keeps named side entries beside its records. The store gives the tag and the entries no meaning: the layer
/// above does (the SBX log: the schemas bundle a record was written with, and the bundles).
/// </summary>
public interface ITaggedBlobStorage<TKey>
	where TKey : notnull, IComparable<TKey>
{
	ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, uint tag, string? json, CancellationToken cancellationToken = default);

	ValueTask<TaggedBlob> ReadTaggedBlobAsync(TKey key, CancellationToken cancellationToken = default);

	/// <summary>Every record after <paramref name="from"/> (all of them when it is default), in key order, with its tag.</summary>
	IAsyncEnumerable<(TKey Key, TaggedBlob Blob)> EnumerateTaggedBlobsAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default);

	/// <summary>Every entry of the side <paramref name="side"/>, by key.</summary>
	ValueTask<IReadOnlyDictionary<string, byte[]>> ReadSideEntriesAsync(string side, CancellationToken cancellationToken = default);

	/// <summary>Stores an entry of the side <paramref name="side"/>; storing the same entry again changes nothing. Cleared with the records.</summary>
	ValueTask StoreSideEntryAsync(string side, string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}

/// <summary>A record's bytes and its tag; null for a record written without one.</summary>
public readonly record struct TaggedBlob(byte[] Blob, uint? Tag);
