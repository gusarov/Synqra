namespace Synqra;

/// <summary>
/// Master → client: the snapshot is complete. The client checks the counts and only now takes <see cref="Watermark"/> as its
/// resume point; the backlog after it follows.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A0A-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.821, "1 SnapshotId Guid Watermark Guid ChunkCount long RecordCount long")]
public partial class SnapshotEnd : TransportOperation
{
	public required partial Guid SnapshotId { get; set; }

	public required partial Guid Watermark { get; set; }

	public required partial long ChunkCount { get; set; }

	public required partial long RecordCount { get; set; }

	public override string ToString() => $"SnapshotEnd({SnapshotId} at {Watermark}, {ChunkCount} chunks, {RecordCount} records)";
}
