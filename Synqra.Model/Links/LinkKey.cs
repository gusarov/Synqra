namespace Synqra;

/// <summary>
/// Identity-independent dedup/upsert key for a link: "is there already an equivalent link
/// between these endpoints?" Distinct from the link's own <see cref="Link.LinkId"/> — two links can
/// never share a <c>linkType</c>+endpoint key, but each still has its own identity. Directed and
/// undirected links differ only in how the endpoint pair folds into this key (ordered vs. unordered).
/// Internal currency of the store's link index; consumer code never constructs one.
/// </summary>
public readonly struct LinkKey : System.IEquatable<LinkKey>
{
	readonly System.Type _linkType;
	readonly System.Guid _x;
	readonly System.Guid _y;

	LinkKey(System.Type linkType, System.Guid x, System.Guid y)
	{
		_linkType = linkType;
		_x = x;
		_y = y;
	}

	/// <summary>
	/// The link's concrete type, and its folded endpoint pair (ordered for a directed key,
	/// canonicalized for an undirected one). Exposed so a store backend without an in-process
	/// dictionary keyed by <see cref="LinkKey"/> (e.g. one that queries a database instead) can
	/// decode an externally-supplied key well enough to look it up. Read-only — construction stays
	/// restricted to <see cref="Directed"/>/<see cref="Undirected"/>.
	/// </summary>
	public System.Type LinkType => _linkType;
	public System.Guid X => _x;
	public System.Guid Y => _y;

	/// <summary>A→B differs from B→A.</summary>
	public static LinkKey Directed(System.Type linkType, System.Guid source, System.Guid target) => new(linkType, source, target);

	/// <summary>{A,B} == {B,A} — endpoints are folded into canonical order so the key is symmetric.</summary>
	public static LinkKey Undirected(System.Type linkType, System.Guid a, System.Guid b)
		=> a.CompareTo(b) <= 0 ? new(linkType, a, b) : new(linkType, b, a);

	public bool Equals(LinkKey other) => _linkType == other._linkType && _x == other._x && _y == other._y;
	public override bool Equals(object? obj) => obj is LinkKey other && Equals(other);

	public override int GetHashCode()
	{
		unchecked
		{
			var hash = _linkType?.GetHashCode() ?? 0;
			hash = (hash * 397) ^ _x.GetHashCode();
			hash = (hash * 397) ^ _y.GetHashCode();
			return hash;
		}
	}

	public static bool operator ==(LinkKey left, LinkKey right) => left.Equals(right);
	public static bool operator !=(LinkKey left, LinkKey right) => !left.Equals(right);
}
