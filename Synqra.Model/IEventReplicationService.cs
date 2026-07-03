
namespace Synqra;

public interface IEventReplicationService
{
	bool IsOnline { get; }

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