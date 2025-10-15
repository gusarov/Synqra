
namespace Synqra;

public class CommandCreatedEvent : Event
{
	public required Command Data { get; init; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
