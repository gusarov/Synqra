
namespace Synqra;

public interface IEventReplicationService
{
	bool IsOnline { get; }

	/// <summary>
	/// The streams this connection is currently subscribed to, as last confirmed by the master's
	/// subscription-state ack (sent after HELLO and every Subscribe/Unsubscribe). A client can compare
	/// this against what it asked for to detect an unexpected server default. Empty until the first ack.
	/// </summary>
	IReadOnlyCollection<Guid> ActiveStreams { get; }

	/// <summary>Raised when the master confirms a change to <see cref="ActiveStreams"/>.</summary>
	event Action? SubscriptionChanged;

	/// <summary>
	/// Asks the master to start delivering a stream this connection is authorized to read (its own
	/// stream or a host-granted shared stream). The master authorizes the request, replays that
	/// stream's backlog, and acks the resulting <see cref="ActiveStreams"/>. A stream the host does not
	/// authorize is silently not added — observe <see cref="ActiveStreams"/> to confirm it took.
	/// </summary>
	Task SubscribeAsync(Guid streamId, CancellationToken ct = default);

	/// <summary>Asks the master to stop delivering a stream (removing it from <see cref="ActiveStreams"/>).</summary>
	Task UnsubscribeAsync(Guid streamId, CancellationToken ct = default);

	/// <summary>
	/// Raised after one or more incoming server events have been appended to this client's local
	/// durable event log. This service is transport-only — it does not touch any projection. The
	/// projection owner (test node / client component) subscribes and brings its stream's projection
	/// up to date via <see cref="IProjectionKeeper.MaintainAsync"/> (typically through
	/// <see cref="IProjectionProvider.GetAsync"/>). The signal is stream-agnostic; each owner catches
	/// up only its own stream (a no-op if nothing new arrived for it).
	/// </summary>
	event Action? EventsReceived;

	void Trigger(Command command, IReadOnlyList<Event> events);

	/// <summary>
	/// Wipes this client's local durable event log, if the underlying storage supports it
	/// (see <see cref="Synqra.AppendStorage.IClearableAppendStorage"/>) — a no-op otherwise.
	/// Does not itself reconnect or reload anything: this is Synqra core and has no Blazor
	/// dependency, so a caller (e.g. a "Force Resync" button) is responsible for reloading
	/// the app afterward to actually rebuild state from the server's fully-synced backlog.
	/// </summary>
	Task ClearLocalStorageAsync();
}