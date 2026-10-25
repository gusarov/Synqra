namespace Synqra;

/// <summary>
/// The master's say over a peer's event before it is applied: a reason refuses it (the sender is told with
/// <see cref="EventRejected"/> and moves past it), null lets it through. Only events that arrive over a replication socket pass
/// through a guard; the server's own writes (seeders, jobs, the host's API) do not, so what only automation may change stays
/// changeable by it. Runs in the connection's stream scope (<see cref="SynqraStreamContext"/>).
/// </summary>
public interface IInboundEventGuard
{
	ValueTask<string?> RefuseAsync(Event ev, CancellationToken cancellationToken = default);
}
