using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Event emitted in response to a successful
/// <see cref="ChangeComponentPropertyCommand"/>. Applied by the projection by
/// locating the addressed component and invoking the same bindable-set path
/// that the generator-emitted setter uses (so <c>OnPropertyChanged</c> fires
/// naturally on listeners).
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-8E02-000000000000")]
[Schema(2026.405, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid ComponentTypeId Guid ComponentId Guid PropertyName string OldValue object? NewValue object?")]
[Schema(2026.740, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid ComponentTypeId Guid ComponentId Guid PropertyName string OldValue object? NewValue object? BaseEventId Guid")]
public partial class ComponentPropertyChangedEvent : SingleObjectEvent
{
	public partial System.Guid ComponentTypeId { get; set; }
	public partial System.Guid ComponentId { get; set; }
	public required partial string PropertyName { get; set; }
	public partial object? OldValue { get; set; }
	public partial object? NewValue { get; set; }

	// Copied from the command: what the writer had applied when it wrote. Persisted so a later fold can tell a sequential write from a concurrent one.
	public partial System.Guid BaseEventId { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
