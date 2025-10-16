namespace Synqra;

[SynqraModel]
[Schema(2025.794, "1 PropertyName string OldValue object? NewValue object? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid-")]
[Schema(2025.795, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid PropertyName string OldValue object? NewValue object?")]
[Schema(2025.796, "1 PropertyName string OldValue object? NewValue object? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.797, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid PropertyName string OldValue object? NewValue object?")]
[Schema(2025.798, "1 PropertyName string OldValue object? NewValue object? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.799, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid PropertyName string OldValue object? NewValue object?")]
public partial class ObjectPropertyChangedEvent : SingleObjectEvent
{
	public required partial string PropertyName { get; set; }
	public partial object? OldValue { get; set; }
	public partial object? NewValue { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
