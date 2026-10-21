namespace Synqra;

/// <summary>
/// Master → client: the event the client sent is in the master's log, appended now or already there. The client moves
/// its outbound cursor on this alone: a frame written into a dying socket was never sent until the master says so.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A06-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.802, "1 EventId Guid")]
public partial class EventAccepted : TransportOperation
{
	public required partial Guid EventId { get; set; }

	public override string ToString() => $"EventAccepted({EventId})";
}
