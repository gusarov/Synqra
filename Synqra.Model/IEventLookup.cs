namespace Synqra;

/// <summary>
/// A log that can say whether it holds an event by its identity. The replication endpoint asks before applying a peer's
/// event: one the log holds already (a resend after a lost acknowledgement, offered again to a restarted process whose
/// in-memory set is empty) is acknowledged, never applied a second time.
/// </summary>
public interface IEventLookup
{
	Task<bool> ContainsAsync(Guid streamId, Guid eventId, CancellationToken cancellationToken = default);
}
