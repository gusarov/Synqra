namespace Synqra.BlobStorage.IndexedDb;

/// <summary>One page of records as the JS module returns it: <see cref="Keys"/>[i] is the key text of <see cref="Blobs"/>[i].</summary>
public sealed class BlobPage
{
	public string[] Keys { get; set; } = [];

	public byte[][] Blobs { get; set; } = [];
}
