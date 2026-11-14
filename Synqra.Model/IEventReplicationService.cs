
namespace Synqra;

public interface IEventReplicationService
{
	bool IsOnline { get; }

	/// <summary>
	/// Whether the master has sent this connection everything it held when the connection opened (its snapshot and backlog):
	/// false while offline and while a catch-up arrives, which every reconnect starts again. A host with no master has nothing to
	/// wait for. Its changes raise <see cref="StatusChanged"/>.
	/// </summary>
	bool IsCaughtUp => true;

	/// <summary>Event frames the current connection received, known ones included: during a catch-up, how far it has come.</summary>
	long ConnectionFrames => 0;

	/// <summary>
	/// Frames this run could not read: they carry a type this build does not know, as a newer server's do for a page left open
	/// across a deploy. Each is stepped over, and the watermark stays before the first, so a reload on the newer build is sent
	/// them. Above zero, a host should offer that reload. Its first change raises <see cref="StatusChanged"/>.
	/// </summary>
	long UnreadableFrames => 0;

	/// <summary>The build the master last told this client (<see cref="ServerVersion"/>); null before it did, or when it names none.</summary>
	string? ServerBuild => null;

	/// <summary>
	/// Whether the master asked this page to reload: it runs another build than the one this run started with, or it raised a reload
	/// demand. A host reloads once <see cref="HasUnconfirmedWrites"/> is false. Its change raises <see cref="StatusChanged"/>.
	/// </summary>
	bool ReloadRequired => false;

	/// <summary>
	/// Whether a local write may not have reached the master yet (unsent, or sent and not acknowledged): a reload now could lose it.
	/// Its changes raise <see cref="StatusChanged"/>.
	/// </summary>
	bool HasUnconfirmedWrites => false;

	/// <summary>
	/// The master's position this client has reached: the resume point of the last frame it took (a v7 sequence, so it also tells
	/// how far into the stream's history a replay is). Default before the first.
	/// </summary>
	Guid ServerWatermark => default;

	/// <summary>
	/// When the next connect attempt is due while the client waits between attempts, so a UI can show "retrying in N s": null
	/// while connected and while an attempt is under way. Its changes raise <see cref="StatusChanged"/>.
	/// </summary>
	DateTimeOffset? NextAttemptAt => null;

	/// <summary>
	/// Connects again at once when the connection looks dead: cuts short the pause before the next attempt, or ends a connect
	/// attempt or a connection that is not caught up and has heard nothing for two seconds (counted from the attempt's start until
	/// the master's HELLO reply, from the last frame after it), and opens a new one, which resumes from the watermark. Online and
	/// caught up, it asks the master whether it is still there and cuts the connection when nothing answers (after a sleep or a
	/// network change a connection can be dead without a close). A no-op while the master prepares its HELLO reply (seeding, cutting
	/// a snapshot) for under two seconds, and while a catch-up is still receiving frames: safe to call at any time, e.g. on every tab
	/// return or network change.
	/// </summary>
	void ReconnectNow()
	{
	}

	/// <summary>Raised when <see cref="IsOnline"/>, <see cref="IsCaughtUp"/> or <see cref="NextAttemptAt"/> changes, on the replication's own thread.</summary>
	event Action? StatusChanged
	{
		add
		{
		}
		remove
		{
		}
	}

	/// <summary>
	/// The streams this connection is currently subscribed to, as last confirmed by the master's
	/// subscription-state ack (sent after HELLO and every Subscribe/Unsubscribe). A client can compare
	/// this against what it asked for to detect an unexpected server default. Empty until the first ack.
	/// A plain polled property — poll it after a Subscribe/Unsubscribe to confirm it took (matches how
	/// <see cref="IsOnline"/> is consumed); no change notification is raised.
	/// </summary>
	IReadOnlyCollection<Guid> ActiveStreams { get; }

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
	/// Wipes this client's local durable event log and its replication cursors, and ends this run: nothing it receives or
	/// sends afterwards is stored, so the next start replays the whole stream into an empty log. A log whose storage cannot
	/// be wiped throws (see <see cref="Synqra.AppendStorage.IClearableAppendStorage"/>). Does not reload anything: this is
	/// Synqra core and has no Blazor dependency, so a caller (e.g. a "Force Resync" button) reloads the app afterward.
	/// </summary>
	Task ClearLocalStorageAsync();
}