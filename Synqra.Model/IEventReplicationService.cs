
namespace Synqra;

public interface IEventReplicationService
{
	bool IsOnline { get; }

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