using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Event emitted in response to a successful <see cref="AddComponentCommand"/>.
/// The projection applies it by instantiating the component, attaching it to
/// the container's <see cref="IComponentContainer.Components"/> collection,
/// and (when applicable) firing <see cref="IActivatableComponent.Activate"/>.
/// </summary>
[SynqraModel]
[Schema(2026.405, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid ComponentTypeId Guid ComponentId Guid Data ObjectData")]
public partial class ComponentAddedEvent : SingleObjectEvent
{
	public partial System.Guid ComponentTypeId { get; set; }
	public partial System.Guid ComponentId { get; set; }

	/// <summary>Canonical property bag for the component's payload — see <see cref="AddComponentCommand.Data"/>'s remarks.</summary>
	public required partial ObjectData Data { get; set; }

	/// <summary>The in-process create path's live instance — see <see cref="AddComponentCommand.LiveComponent"/>'s remarks. Never serialized.</summary>
	[JsonIgnore]
	public object? LiveComponent { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
