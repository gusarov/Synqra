namespace Synqra;

/// <summary>
/// A point in the model graph an edge can terminate at: an object, optionally narrowed to one
/// of its components. Object-to-object links (the common case) leave the component fields
/// default. Value-based equality so instances key adjacency tables.
/// <para>
/// Deliberately has no port/channel concept — that is routing-specific (which named output on
/// a component feeds which named input) and belongs on the consumer's concrete edge type, not
/// on the framework's endpoint addressing. See plans/edges.md.
/// </para>
/// </summary>
public readonly record struct Ref(
	Guid ObjectId,
	Guid ComponentTypeId = default,
	Guid ComponentId = default)
{
	/// <summary>True when this endpoint addresses the object itself, not a specific component on it.</summary>
	public bool IsObjectScoped => ComponentTypeId == default && ComponentId == default;

	public bool IsDefault => ObjectId == default && IsObjectScoped;
}

/// <summary>Canonical ordering over <see cref="Ref"/>, used to normalize undirected edge keys.</summary>
internal static class RefOrder
{
	public static int Compare(Ref a, Ref b)
	{
		var c = a.ObjectId.CompareTo(b.ObjectId);
		if (c != 0) return c;
		c = a.ComponentTypeId.CompareTo(b.ComponentTypeId);
		return c != 0 ? c : a.ComponentId.CompareTo(b.ComponentId);
	}
}
