using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Update one property on a component attached to the container identified by
/// <see cref="SingleObjectCommand.TargetId"/>.
/// <para>
/// The component is addressed either by <see cref="ComponentId"/> (non-unique
/// components) or by <see cref="ComponentTypeId"/> alone (unique components,
/// <see cref="ComponentId"/> = <see cref="System.Guid.Empty"/>).
/// </para>
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-8C02-000000000000")]
[Schema(2026.405, "1 CommandId Guid StreamId Guid TargetTypeId Guid CollectionId Guid TargetId Guid ComponentTypeId Guid ComponentId Guid PropertyName string OldValue object? NewValue object?")]
public partial class ChangeComponentPropertyCommand : SingleObjectCommand
{
	public partial System.Guid ComponentTypeId { get; set; }
	public partial System.Guid ComponentId { get; set; }

	public required partial string PropertyName { get; set; }
	public partial object? OldValue { get; set; }
	public partial object? NewValue { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
