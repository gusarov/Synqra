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
[Schema(2026.405, "1 CommandId Guid StreamId Guid TargetTypeId Guid CollectionId Guid TargetId Guid ComponentTypeId Guid ComponentId Guid Data object?")]
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
	/// Initial payload for the component. Interpreted by the projection's
	/// type-aware deserialization path; treated as opaque on the wire.
	/// </summary>
	public partial object? Data { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
