namespace Synqra;

/// <summary>
/// Master → client: the answer to a <see cref="CatchUpRequest"/>. Everything the master held for the connection when it opened
/// (its snapshot and backlog) was sent before it; what follows is live.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A0F-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.867, "1 RequestId Guid")]
public partial class CaughtUp : TransportOperation
{
	/// <summary>The <see cref="CatchUpRequest.RequestId"/> answered.</summary>
	public required partial Guid RequestId { get; set; }

	public override string ToString() => $"CaughtUp({RequestId})";
}
