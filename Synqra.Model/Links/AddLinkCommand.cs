using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Create a new <see cref="Link"/> between two objects. Dedicated to links the same way
/// <see cref="AddComponentCommand"/> is dedicated to components, instead of riding
/// <see cref="CreateObjectCommand"/> — a link is an attached fact, not a top-level object.
/// <para>
/// <see cref="SourceId"/>/<see cref="TargetId"/> are mandatory on every link — declared on the
/// <see cref="Link"/> base itself, not subtype-specific payload — so they are explicit fields
/// here, exactly the way <see cref="AddComponentCommand.ComponentId"/> is explicit rather than
/// something a caller has to dig out of <see cref="Data"/>. Structural operations (dedup,
/// indexing) read these two fields directly; they never need to materialize <see cref="Data"/>
/// just to learn what a link connects.
/// </para>
/// <para>
/// <see cref="Data"/> carries the link instance itself: the live object when submitted locally
/// (matching <see cref="AddComponentCommand.Data"/>'s contract — which also redundantly carries
/// the component's own id), or its serialized form on replay, resolved via <see cref="LinkTypeId"/>.
/// This is where a concrete link subtype's <i>own</i> properties (e.g. <c>WeightedLink.Order</c>)
/// travel; <see cref="SourceId"/>/<see cref="TargetId"/> being on the link instance too is
/// redundant with the explicit fields above, not a second source of truth — the explicit fields
/// win (see <see cref="LinkAddedEvent"/>'s remarks).
/// </para>
/// </summary>
[SynqraModel]
[Schema(2026.501, "1 CommandId Guid StreamId Guid LinkTypeId Guid LinkId Guid SourceId Guid TargetId Guid Data object?")]
public partial class AddLinkCommand : Command
{
	/// <summary>Synqra type-id of the concrete link class being created.</summary>
	public partial System.Guid LinkTypeId { get; set; }

	/// <summary>Identity of the new link. Allocated by the caller, or by the projection if left default.</summary>
	public partial System.Guid LinkId { get; set; }

	/// <summary>Identity of the object at the link's source/from end. Mandatory.</summary>
	public partial System.Guid SourceId { get; set; }

	/// <summary>Identity of the object at the link's target/to end. Mandatory.</summary>
	public partial System.Guid TargetId { get; set; }

	/// <summary>The link instance (or its rehydrated form on replay) — carries the concrete subtype's own properties, if any.</summary>
	public partial object? Data { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
