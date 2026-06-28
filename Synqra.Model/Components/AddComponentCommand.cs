using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Attach a new component to the container identified by
/// <see cref="SingleObjectCommand.TargetId"/>.
/// <para>
/// <see cref="ComponentTypeId"/> identifies the concrete component class via
/// Synqra's existing type-id mechanism. <see cref="ComponentId"/> is set for
/// addressable (non-unique) components; for unique components it stays
/// <see cref="System.Guid.Empty"/> and the (container, unique-interface) pair
/// addresses the instance.
/// </para>
/// </summary>
[SynqraModel]
[Schema(2026.405, "1 CommandId Guid StreamId Guid TargetTypeId Guid CollectionId Guid TargetId Guid ComponentTypeId Guid ComponentId Guid Data ObjectData")]
public partial class AddComponentCommand : SingleObjectCommand
{
	/// <summary>Synqra type-id of the component implementation being attached.</summary>
	public partial System.Guid ComponentTypeId { get; set; }

	/// <summary>
	/// Identity of the new component, empty for unique components.
	/// For non-unique components a v7 GUID should be allocated by the caller
	/// (or the projection if left default).
	/// </summary>
	public partial System.Guid ComponentId { get; set; }

	/// <summary>
	/// Canonical property bag for the component's payload — see <see cref="ObjectData"/>. Interpreted
	/// by the projection's type-aware hydration path on replay; on the in-process create path,
	/// <see cref="LiveComponent"/> is used instead (see its remarks for why).
	/// </summary>
	public required partial ObjectData Data { get; set; }

	/// <summary>
	/// The actual component instance the caller is holding, for the in-process create path. Unlike
	/// <see cref="CreateObjectCommand.Data"/>/<see cref="ObjectCreatedEvent"/> (where the collection's
	/// Add() attaches the live instance before submitting, so the projection's own attach-tracking
	/// already resolves back to it), components have no equivalent pre-attach step — the projection
	/// must get the caller's actual reference back from here so its generated property setters keep
	/// routing subsequent writes through the store instead of silently mutating an orphaned copy.
	/// Never serialized; null means "replay, rebuild purely from <see cref="Data"/>".
	/// </summary>
	[JsonIgnore]
	public object? LiveComponent { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
