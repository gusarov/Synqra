using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// A wire was attached. The projection materializes a <see cref="Wire"/> in its
/// in-memory state and indexes it for routing lookups.
/// </summary>
[SynqraModel]
[Schema(2026.406, "1 EventId Guid CommandId Guid WireId Guid SourceContainerId Guid SourceComponentTypeId Guid SourceComponentId Guid SourcePortName string TargetContainerId Guid TargetComponentTypeId Guid TargetComponentId Guid TargetPortName string Type int")]
public partial class WireAddedEvent : Event
{
	public partial System.Guid WireId { get; set; }

	public partial System.Guid SourceContainerId { get; set; }
	public partial System.Guid SourceComponentTypeId { get; set; }
	public partial System.Guid SourceComponentId { get; set; }
	public required partial string SourcePortName { get; set; }

	public partial System.Guid TargetContainerId { get; set; }
	public partial System.Guid TargetComponentTypeId { get; set; }
	public partial System.Guid TargetComponentId { get; set; }
	public required partial string TargetPortName { get; set; }

	public partial int Type { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
