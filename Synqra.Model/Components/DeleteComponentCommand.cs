using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Detach a component from its container.
/// <para>
/// Addressing rules match <see cref="ChangeComponentPropertyCommand"/> — by
/// <see cref="ComponentId"/> for non-unique components, by
/// <see cref="ComponentTypeId"/> alone for unique ones.
/// </para>
/// </summary>
[SynqraModel]
[Schema(2026.405, "1 CommandId Guid StreamId Guid TargetTypeId Guid CollectionId Guid TargetId Guid ComponentTypeId Guid ComponentId Guid")]
public partial class DeleteComponentCommand : SingleObjectCommand
{
	public partial System.Guid ComponentTypeId { get; set; }
	public partial System.Guid ComponentId { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
