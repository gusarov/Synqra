
namespace Synqra;

public interface IEventReplicationService
{
	bool IsOnline { get; }

	void Trigger(Command command, IReadOnlyList<Event> events);
}