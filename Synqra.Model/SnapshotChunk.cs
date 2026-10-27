namespace Synqra;

/// <summary>
/// Master → client: one chunk of a snapshot. <see cref="Payload"/> holds <see cref="Count"/> records written in one SBX
/// context with the header's map, so a chunk decodes alone and can be stored as received.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A09-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.821, "1 SnapshotId Guid Index long Count long Payload byte[]")]
public partial class SnapshotChunk : TransportOperation
{
	public required partial Guid SnapshotId { get; set; }

	/// <summary>Zero-based; chunks arrive in order.</summary>
	public required partial long Index { get; set; }

	public required partial long Count { get; set; }

	public required partial byte[] Payload { get; set; }

	public override string ToString() => $"SnapshotChunk({SnapshotId} #{Index}, {Count} records, {Payload?.Length} B)";
}
