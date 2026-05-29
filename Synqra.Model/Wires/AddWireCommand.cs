using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Attach a wire connecting two component ports. The projection creates a
/// <see cref="Wire"/> with a generated id and stores it under the Wires
/// collection. If a wire with identical source + target + type already
/// exists, the projection treats the add as idempotent (no-op event).
/// </summary>
[SynqraModel]
[Schema(2026.406, "1 CommandId Guid StreamId Guid WireId Guid SourceContainerId Guid SourceComponentTypeId Guid SourceComponentId Guid SourcePortName string TargetContainerId Guid TargetComponentTypeId Guid TargetComponentId Guid TargetPortName string Type int")]
public partial class AddWireCommand : Command
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

	/// <summary>Port type — stored as int so legacy versions of the serializer round-trip cleanly.</summary>
	public partial int Type { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
