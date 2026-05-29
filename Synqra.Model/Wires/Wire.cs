using System;

namespace Synqra;

/// <summary>
/// A typed connection between two component ports. The substrate stores wires
/// as first-class state — the routing engine reads them to decide where a value
/// emitted on a source port should land. v0 carries <see cref="PortType.Event"/>
/// only; non-Event port types stub the type system but have no runtime yet.
/// </summary>
[SynqraModel]
[Schema(2026.406, "1 Id Guid SourceContainerId Guid SourceComponentTypeId Guid SourceComponentId Guid SourcePortName string? TargetContainerId Guid TargetComponentTypeId Guid TargetComponentId Guid TargetPortName string? Type int")]
public partial class Wire : IIdentifiable<Guid>
{
	/// <summary>Stable identity. The projection assigns this on AddWireCommand.</summary>
	public partial Guid Id { get; set; }

	/// <summary>Port emitting values on this wire.</summary>
	public partial Guid SourceContainerId { get; set; }
	public partial Guid SourceComponentTypeId { get; set; }
	public partial Guid SourceComponentId { get; set; }
	public partial string? SourcePortName { get; set; }

	/// <summary>Port receiving values from this wire.</summary>
	public partial Guid TargetContainerId { get; set; }
	public partial Guid TargetComponentTypeId { get; set; }
	public partial Guid TargetComponentId { get; set; }
	public partial string? TargetPortName { get; set; }

	/// <summary>
	/// Channel type. v0 only ships <see cref="PortType.Event"/>; declaring other
	/// types now means future runtimes for them can light up without re-shaping
	/// the wire model. Stored as int (Synqra binary serializer doesn't know enums
	/// natively yet).
	/// </summary>
	public partial int Type { get; set; }

	/// <summary>Convenience accessor for <see cref="Type"/> as <see cref="PortType"/>. Read-only; set <see cref="Type"/> directly.</summary>
	public PortType PortType => (PortType)Type;

	Guid IIdentifiable<Guid>.Id => Id;

	/// <summary>Source port as a value tuple.</summary>
	public PortRef Source => new(SourceContainerId, SourceComponentTypeId, SourceComponentId, SourcePortName ?? string.Empty);

	/// <summary>Target port as a value tuple.</summary>
	public PortRef Target => new(TargetContainerId, TargetComponentTypeId, TargetComponentId, TargetPortName ?? string.Empty);
}
