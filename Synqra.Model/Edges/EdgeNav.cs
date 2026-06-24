namespace Synqra;

/// <summary>
/// Hand-written edge-backed navigation — the "simple Parent property" pattern. A model exposes
/// <c>node.Parent</c>/<c>node.Children</c> ergonomics by delegating a regular (non-partial,
/// unpersisted) property to one of these one-liners, instead of hand-rolling dual-direction id
/// lists with a lazy "_lookup" toggle. A future generator (see plans/edges.md, step 4) can emit
/// the same call from an <c>[EdgeCollection]</c> attribute; this is the v1, hand-written version
/// of the exact same idea — not a placeholder for it.
/// </summary>
public static class EdgeNav
{
	/// <summary>The (at most one) edge of type <typeparamref name="TEdge"/> pointing at <paramref name="node"/>, resolved to its source object.</summary>
	public static T? SingleParent<T, TEdge>(T node) where T : class where TEdge : DirectedEdge
		=> Parents<T, TEdge>(node).FirstOrDefault();

	/// <summary>All objects with a <typeparamref name="TEdge"/> pointing at <paramref name="node"/>.</summary>
	public static IReadOnlyList<T> Parents<T, TEdge>(T node) where T : class where TEdge : DirectedEdge
	{
		if (!TryGetIndex(node, out var store, out var index, out var me)) return [];
		return index.EdgesTo(me).OfType<TEdge>()
			.Select(e => Resolve<T>(store, e.A.ObjectId))
			.OfType<T>()
			.ToArray();
	}

	/// <summary>All objects <paramref name="node"/> points at via a <typeparamref name="TEdge"/>.</summary>
	public static IReadOnlyList<T> Children<T, TEdge>(T node) where T : class where TEdge : DirectedEdge
	{
		if (!TryGetIndex(node, out var store, out var index, out var me)) return [];
		return index.EdgesFrom(me).OfType<TEdge>()
			.Select(e => Resolve<T>(store, e.B.ObjectId))
			.OfType<T>()
			.ToArray();
	}

	/// <summary>All objects connected to <paramref name="node"/> via a <typeparamref name="TEdge"/>, either side.</summary>
	public static IReadOnlyList<T> Related<T, TEdge>(T node) where T : class where TEdge : UndirectedEdge
	{
		if (!TryGetIndex(node, out var store, out var index, out var me)) return [];
		return index.EdgesFrom(me).OfType<TEdge>()
			.Select(e => e.A == me ? e.B : e.A)
			.Select(other => Resolve<T>(store, other.ObjectId))
			.OfType<T>()
			.ToArray();
	}

	static bool TryGetIndex<T>(T node, out IObjectStore store, out IEdgeIndex index, out Ref me) where T : class
	{
		store = null!;
		index = null!;
		me = default;
		if (node is not IBindableModel { Store: { } attachedStore } || attachedStore is not IEdgeIndex edgeIndex)
		{
			return false;
		}
		store = attachedStore;
		index = edgeIndex;
		me = new Ref(attachedStore.GetId(node));
		return true;
	}

	static T? Resolve<T>(IObjectStore store, Guid id) where T : class
		=> store.GetCollection<T>().FirstOrDefault(n => store.GetId(n) == id);
}
