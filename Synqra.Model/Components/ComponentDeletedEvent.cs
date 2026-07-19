using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Event emitted in response to a successful <see cref="DeleteComponentCommand"/>.
/// The projection applies it by removing the addressed component from the
/// container's <see cref="IComponentContainer.Components"/> collection.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-8E03-000000000000")]
[Schema(2026.405, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid ComponentTypeId Guid ComponentId Guid")]
public partial class ComponentDeletedEvent : SingleObjectEvent
{
	public partial System.Guid ComponentTypeId { get; set; }
	public partial System.Guid ComponentId { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
