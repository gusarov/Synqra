namespace Synqra;

/// <summary>
/// Registered by a client whose projection folds a stream snapshot (<see cref="StreamSnapshotEvent"/>); the in-memory store
/// registers it. A replication client asks for a snapshot only when it is registered: with a projection that ignores the record,
/// the client would move its watermark past the stream's whole history and show an empty stream.
/// </summary>
public sealed class StreamSnapshotFolding
{
}
