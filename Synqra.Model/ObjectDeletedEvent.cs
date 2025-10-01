namespace Synqra;

public class ObjectDeletedEvent : SingleObjectEvent
{
	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
