using System;

namespace Synqra;

/// <summary>
/// A point in the graph that a wire can terminate at — the (container, component,
/// port-name) triple. Components and ports live inside containers, so resolving a
/// PortRef back to a concrete instance requires the projection's lookup.
/// <para>
/// Modelled as a record struct so equality is value-based and instances can be
/// used as dictionary keys for wire-routing tables.
/// </para>
/// </summary>
public readonly record struct PortRef(
	Guid ContainerId,
	Guid ComponentTypeId,
	Guid ComponentId,
	string PortName)
{
	/// <summary>True when this <see cref="PortRef"/> has no addressable target.</summary>
	public bool IsDefault => ContainerId == Guid.Empty
		&& ComponentTypeId == Guid.Empty
		&& ComponentId == Guid.Empty
		&& string.IsNullOrEmpty(PortName);
}
