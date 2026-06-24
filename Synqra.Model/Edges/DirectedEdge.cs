namespace Synqra;

/// <summary>
/// Edge where A→B differs from B→A. A is the source/from end, B is the target/to end. Adds no
/// stored fields of its own, so — like <see cref="ObjectDeletedEvent"/> — it is a plain
/// subclass, not <c>[SynqraModel]</c>; it just inherits <see cref="Edge"/>'s bindable plumbing.
/// </summary>
public abstract class DirectedEdge : Edge
{
	public override EdgeKey StructuralKey => EdgeKey.Directed(GetType(), A, B);
}
