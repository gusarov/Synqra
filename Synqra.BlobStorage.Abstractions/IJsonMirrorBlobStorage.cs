namespace Synqra.BlobStorage;

/// <summary>
/// Optional capability an <see cref="IBlobStorage{TKey}"/> can implement: write a blob plus
/// a human-readable JSON rendering of the same item into the same record (one store, one
/// key — not a second blob), for backends where the binary format has no schema-evolution
/// story yet and being able to eyeball the raw record matters.
/// </summary>
public interface IJsonMirrorBlobStorage<TKey>
	where TKey : notnull, IComparable<TKey>
{
	bool WantsJsonMirror { get; }

	ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, string json, CancellationToken cancellationToken = default);
}
