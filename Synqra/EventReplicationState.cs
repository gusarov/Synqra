namespace Synqra;

/// <summary>
/// Per-node replication bookkeeping. <see cref="ServerWatermark"/> is the server's watermark this node has caught up
/// to (every committed sequence at or below it is in the local log); it is what HELLO sends, so a persisted state
/// (see <see cref="IEventReplicationStateStore"/>) resumes with the delta instead of the whole stream. The server
/// dedups by EventId regardless, so a stale or cold value only costs re-sent frames, never correctness.
/// </summary>
public class EventReplicationState
{
	public Guid MyNodeId { get; set; } = Guid.NewGuid();
	public Guid LastEventIdFromMe { get; set; }
	public Guid ServerWatermark { get; set; }
}
