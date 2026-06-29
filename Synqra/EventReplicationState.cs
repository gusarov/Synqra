namespace Synqra;

/// <summary>
/// Per-node replication bookkeeping (node id, last-seen event cursors). In-memory only —
/// a fresh node id and cursor each process start. Correctness is unaffected: the server's
/// master endpoint dedups by EventId regardless, so a "cold" cursor after a restart only
/// means a handful of already-known events get briefly re-sent and dropped, not anything
/// incorrect. Deliberately not persisted to a file (or anywhere else): file I/O doesn't
/// exist in a real browser WASM sandbox — EventReplicationService's main real-world
/// consumer — and a file-backed implementation isn't unit-testable without a real
/// filesystem, when the entire point of this class is to be a small, swappable piece of
/// state.
/// </summary>
public class EventReplicationState
{
	public Guid MyNodeId { get; set; } = Guid.NewGuid();
	public Guid LastEventIdFromMe { get; set; }
	public Guid LastEventIdFromServer { get; set; }
}
