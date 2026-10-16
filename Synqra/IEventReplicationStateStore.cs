namespace Synqra;

/// <summary>
/// Where a client keeps its <see cref="EventReplicationState"/> between runs, keyed by the stream the connection
/// serves. The host decides the medium (a browser: localStorage); without one the state lives for the process only
/// and every start asks the server for the whole stream again.
/// </summary>
public interface IEventReplicationStateStore
{
	Task<EventReplicationState?> LoadAsync(Guid streamId, CancellationToken cancellationToken = default);

	Task SaveAsync(Guid streamId, EventReplicationState state, CancellationToken cancellationToken = default);
}
