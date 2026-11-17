namespace Synqra.AppendStorage.BlobStorage;

/// <summary>Where a storage keeps its schema sets beside its records: the key of a set id, and which keys hold sets.</summary>
public sealed class SchemaSetKeys<TKey>(Func<uint, TKey> createKey, Func<TKey, bool> isSetKey)
{
	public TKey CreateKey(uint setId) => createKey(setId);

	public bool IsSetKey(TKey key) => isSetKey(key);
}
