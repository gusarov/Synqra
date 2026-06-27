namespace Synqra;

/// <summary>
/// Link where A→B differs from B→A: <typeparamref name="TSource"/> is the from/source end,
/// <typeparamref name="TTarget"/> is the to/target end. Concrete relation kinds derive from this
/// (e.g. <c>class HierarchyLink : DirectedLink&lt;TodoNode, TodoNode&gt;</c>); the concrete type is the
/// discriminator that distinguishes "parent-of" from "depends-on". The structural key is ordered.
/// </summary>
public abstract class DirectedLink<TSource, TTarget> : Link<TSource, TTarget>
	where TSource : class
	where TTarget : class
{
	public override LinkKey StructuralKey => LinkKey.Directed(GetType(), SourceId, TargetId);
}
