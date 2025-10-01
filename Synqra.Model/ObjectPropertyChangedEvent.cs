namespace Synqra;

public class ObjectPropertyChangedEvent : SingleObjectEvent
{
	public required string PropertyName { get; init; }
	public object? OldValue { get; set; }
	public object? NewValue { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
