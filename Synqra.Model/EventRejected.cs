namespace Synqra;

/// <summary>
/// Master → client: the event the client sent was refused by the master's projection and will never be in its log (a
/// facet for a slot already filled, a link that already exists). The client moves its outbound cursor past it, as on
/// <see cref="EventAccepted"/>, so a refused event is not offered again on every connect; its local effect stays until the
/// master's own events for the same state arrive and win.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A07-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.807, "1 EventId Guid Reason string")]
public partial class EventRejected : TransportOperation
{
	public required partial Guid EventId { get; set; }

	/// <summary>Why the master refused it: the projection's own words, for the client's log.</summary>
	public required partial string Reason { get; set; }

	public override string ToString() => $"EventRejected({EventId}: {Reason})";
}
