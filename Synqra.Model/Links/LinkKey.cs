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
	readonly System.Guid _qualifier;

	LinkKey(System.Type linkType, System.Guid x, System.Guid y, System.Guid qualifier)
	{
		_linkType = linkType;
		_x = x;
		_y = y;
		_qualifier = qualifier;
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

	/// <summary>Tells apart links of one type between the same endpoints (e.g. a link's own kind); empty when there is none.</summary>
	public System.Guid Qualifier => _qualifier;

	/// <summary>A→B differs from B→A.</summary>
	public static LinkKey Directed(System.Type linkType, System.Guid source, System.Guid target, System.Guid qualifier = default) => new(linkType, source, target, qualifier);

	/// <summary>{A,B} == {B,A} — endpoints are folded into canonical order so the key is symmetric.</summary>
	public static LinkKey Undirected(System.Type linkType, System.Guid a, System.Guid b, System.Guid qualifier = default)
		=> a.CompareTo(b) <= 0 ? new(linkType, a, b, qualifier) : new(linkType, b, a, qualifier);

	public bool Equals(LinkKey other) => _linkType == other._linkType && _x == other._x && _y == other._y && _qualifier == other._qualifier;
	public override bool Equals(object? obj) => obj is LinkKey other && Equals(other);

	public override int GetHashCode()
	{
		unchecked
		{
			var hash = _linkType?.GetHashCode() ?? 0;
			hash = (hash * 397) ^ _x.GetHashCode();
			hash = (hash * 397) ^ _y.GetHashCode();
			hash = (hash * 397) ^ _qualifier.GetHashCode();
			return hash;
		}
	}

	public static bool operator ==(LinkKey left, LinkKey right) => left.Equals(right);
	public static bool operator !=(LinkKey left, LinkKey right) => !left.Equals(right);
}
