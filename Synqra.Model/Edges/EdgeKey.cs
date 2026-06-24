namespace Synqra;

/// <summary>
/// Identity-independent dedup/upsert key for an edge: "is there already an equivalent edge
/// between these endpoints?" Distinct from <see cref="IIdentifiable{T}.Id"/> (the edge's own
/// store-assigned identity) — two edges can never share a <see cref="Type"/>+endpoint key, but
/// each still has its own identity for replication/deletion. Directed and undirected edges
/// differ only in how the pair folds into this key (ordered vs. unordered).
/// </summary>
public readonly struct EdgeKey : IEquatable<EdgeKey>
{
	readonly Type _edgeType;
	readonly Ref _x;
	readonly Ref _y;

	EdgeKey(Type edgeType, Ref x, Ref y)
	{
		_edgeType = edgeType;
		_x = x;
		_y = y;
	}

	/// <summary>A→B differs from B→A.</summary>
	public static EdgeKey Directed(Type edgeType, Ref source, Ref target) => new(edgeType, source, target);

	/// <summary>{A,B} == {B,A} — endpoints are folded into canonical order so the key is symmetric.</summary>
	public static EdgeKey Undirected(Type edgeType, Ref a, Ref b)
		=> RefOrder.Compare(a, b) <= 0 ? new(edgeType, a, b) : new(edgeType, b, a);

	public bool Equals(EdgeKey other) => _edgeType == other._edgeType && _x.Equals(other._x) && _y.Equals(other._y);
	public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);
	public override int GetHashCode()
	{
		unchecked
		{
			var hash = _edgeType.GetHashCode();
			hash = (hash * 397) ^ _x.GetHashCode();
			hash = (hash * 397) ^ _y.GetHashCode();
			return hash;
		}
	}

	public static bool operator ==(EdgeKey left, EdgeKey right) => left.Equals(right);
	public static bool operator !=(EdgeKey left, EdgeKey right) => !left.Equals(right);
}
