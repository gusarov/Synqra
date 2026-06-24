namespace Synqra;

/// <summary>
/// Adjacency index a projection maintains over <see cref="Edge"/>-derived objects. Replaces
/// hand-rolled dual-direction id lists + a lazy "_lookup" toggle (see plans/edges.md) — the
/// inverse direction is always a query here, never a second stored list.
/// </summary>
public interface IEdgeIndex
{
	/// <summary>Every edge currently known to the index.</summary>
	IReadOnlyCollection<Edge> Edges { get; }

	/// <summary>Directed: edges whose source (<see cref="Edge.A"/>) is <paramref name="r"/>. Undirected: edges incident to <paramref name="r"/>.</summary>
	IReadOnlyList<Edge> EdgesFrom(Ref r);

	/// <summary>Directed: edges whose target (<see cref="Edge.B"/>) is <paramref name="r"/>. Undirected: edges incident to <paramref name="r"/>.</summary>
	IReadOnlyList<Edge> EdgesTo(Ref r);

	/// <summary>Edges connecting <paramref name="a"/> and <paramref name="b"/> in either direction.</summary>
	IReadOnlyList<Edge> EdgesBetween(Ref a, Ref b);

	/// <summary>Looks up the (at most one) edge matching a structural key — the dedup/upsert lookup.</summary>
	bool TryGetByKey(EdgeKey key, out Edge? edge);
}
