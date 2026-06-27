namespace Synqra;

/// <summary>
/// Link where {A,B} == {B,A} — direction carries no meaning. The stored endpoint ids are not
/// reordered at write time; instead <see cref="Link.StructuralKey"/> folds the pair into canonical
/// order, which is the only place "unordered" needs to hold. Index queries treat an undirected link
/// as incident to both endpoints regardless of which one a caller navigates from.
/// </summary>
public abstract class UndirectedLink<TSource, TTarget> : Link<TSource, TTarget>
	where TSource : class
	where TTarget : class
{
	public override LinkKey StructuralKey => LinkKey.Undirected(GetType(), SourceId, TargetId);
}
