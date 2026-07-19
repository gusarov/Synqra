using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Event emitted in response to a successful <see cref="AddComponentCommand"/>.
/// The projection applies it by instantiating the component, attaching it to
/// the container's <see cref="IComponentContainer.Components"/> collection,
/// and (when applicable) firing <see cref="IActivatableComponent.Activate"/>.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-8E01-000000000000")]
[Schema(2026.405, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid ComponentTypeId Guid ComponentId Guid Data object?")]
public partial class ComponentAddedEvent : SingleObjectEvent
{
	public partial System.Guid ComponentTypeId { get; set; }
	public partial System.Guid ComponentId { get; set; }
	public partial object? Data { get; set; }

	/// <summary>In-memory materialized object; [JsonIgnore] so it never reaches the durable log.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public object? DataObject { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
