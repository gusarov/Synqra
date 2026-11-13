namespace Synqra;

/// <summary>
/// Client → master, right after its HELLO: "tell me when you have sent me what you held for me". The master reads it only after
/// the connection's catch-up (its snapshot and backlog), so its answer, <see cref="CaughtUp"/>, follows the last backlog frame.
/// A <see cref="SubscriptionState"/> is no such mark: the HELLO's comes before the backlog.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A0E-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.867, "1 RequestId Guid")]
public partial class CatchUpRequest : TransportOperation
{
	/// <summary>Echoed by the <see cref="CaughtUp"/> that answers it.</summary>
	public required partial Guid RequestId { get; set; }

	public override string ToString() => $"CatchUpRequest({RequestId})";
}
