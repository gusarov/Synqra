
namespace Synqra;

public interface IEventReplicationService
{
	bool IsOnline { get; }

	void Trigger(IReadOnlyList<Event> events);
}