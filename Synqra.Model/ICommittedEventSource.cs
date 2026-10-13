namespace Synqra;

/// <summary>
/// A projection that tells listeners which domain events a locally submitted command produced, once
/// they are applied and durably stored. The server-side replication endpoint uses it to push writes
/// that did not arrive over a socket (a REST call, a background job) to every subscribed client.
/// Events received from a socket and applied directly are not raised here; that path broadcasts
/// itself.
/// </summary>
public interface ICommittedEventSource
{
	event Action<IReadOnlyList<Event>>? EventsCommitted;
}
