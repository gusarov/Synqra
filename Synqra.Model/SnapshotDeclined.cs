namespace Synqra;

/// <summary>
/// Master → client: the snapshot a cold client asked for is not served; the ordinary backlog replay follows instead, so the
/// client always gets an explicit answer to a SubscribeToSnapshot HELLO.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A0B-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.821, "1 StreamId Guid Reason string")]
public partial class SnapshotDeclined : TransportOperation
{
	public required partial Guid StreamId { get; set; }

	/// <summary>Why, for the client's log.</summary>
	public required partial string Reason { get; set; }

	public override string ToString() => $"SnapshotDeclined({StreamId}: {Reason})";
}
