namespace Synqra;

/// <summary>A snapshot header this build cannot read exactly: the snapshot is dropped whole and the stream is replayed instead.</summary>
public class SnapshotSchemaRefusedException : InvalidOperationException
{
	public SnapshotSchemaRefusedException(string message, SnapshotType? entry)
		: base(message)
	{
		Entry = entry;
	}

	/// <summary>The header entry refused; null when the format itself was.</summary>
	public SnapshotType? Entry { get; }
}
