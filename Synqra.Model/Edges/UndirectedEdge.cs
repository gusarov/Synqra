namespace Synqra;

/// <summary>
/// Edge where {A,B} == {B,A} — direction carries no meaning. The stored A/B fields are NOT
/// reordered at write time (the generator-emitted setters give no hook to intercept that); instead
/// <see cref="StructuralKey"/> folds the pair into canonical order, which is the only place
/// "unordered" needs to hold. <see cref="IEdgeIndex"/> queries treat an undirected edge as
/// incident to both A and B regardless of which one a caller queries from.
/// </summary>
public abstract class UndirectedEdge : Edge
{
	public override EdgeKey StructuralKey => EdgeKey.Undirected(GetType(), A, B);
}
