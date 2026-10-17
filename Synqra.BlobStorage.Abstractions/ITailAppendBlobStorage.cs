namespace Synqra.BlobStorage;

/// <summary>What <see cref="ITailAppendBlobStorage{TKey}.AppendAtTailAsync"/> did.</summary>
public enum TailAppendResult
{
	/// <summary>The blob was written under its key.</summary>
	Appended,

	/// <summary>A record with the same unique tag is stored already; nothing was written.</summary>
	Duplicate,

	/// <summary>The store's last key is no longer the expected tail; nothing was written. Read the tail and try again.</summary>
	TailMoved,
}

/// <summary>
/// A blob store several writers may share without talking to each other (browser tabs sharing one IndexedDB): a write
/// lands only at the store's current tail and only once per unique tag, both checked and written in one transaction.
/// </summary>
public interface ITailAppendBlobStorage<TKey> : IBlobStorage<TKey>
	where TKey : notnull, IComparable<TKey>
{
	/// <summary>The store's last key, or default when the store is empty.</summary>
	ValueTask<TKey> ReadTailAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Writes <paramref name="blob"/> under <paramref name="key"/> if the store's last key is still
	/// <paramref name="expectedTail"/> (default: the store is empty) and no record carries <paramref name="uniqueTag"/>.
	/// </summary>
	ValueTask<TailAppendResult> AppendAtTailAsync(TKey key, ReadOnlyMemory<byte> blob, string? json, string uniqueTag, TKey expectedTail, CancellationToken cancellationToken = default);
}
