namespace Synqra;

/// <summary>
/// Adjacency index a store maintains over <see cref="Link"/>-derived objects. Consumer code never
/// touches this — generated navigation properties (<c>[To]</c>/<c>[From]</c>/<c>[Related]</c>) and
/// the runtime link collections query it. Keyed by object identity (<see cref="System.Guid"/>),
/// never by any exposed reference type.
/// </summary>
public interface ILinkIndex
{
	/// <summary>Every link currently known to the index.</summary>
	System.Collections.Generic.IReadOnlyCollection<Link> Links { get; }

	/// <summary>
	/// Links of <paramref name="linkType"/> incident to <paramref name="nodeId"/> at the given
	/// <paramref name="end"/>. <see cref="LinkEnd.Source"/>/<see cref="LinkEnd.Target"/> filter by the
	/// directed source/target; <see cref="LinkEnd.Either"/> returns every incident link.
	/// </summary>
	System.Collections.Generic.IReadOnlyList<Link> LinksAt(System.Guid nodeId, LinkEnd end, System.Type linkType);

	/// <summary>Links of <paramref name="linkType"/> connecting <paramref name="a"/> and <paramref name="b"/> in either direction.</summary>
	System.Collections.Generic.IReadOnlyList<Link> LinksBetween(System.Guid a, System.Guid b, System.Type linkType);

	/// <summary>Looks up the (at most one) link matching a structural key — the dedup/upsert lookup.</summary>
	bool TryGetByKey(LinkKey key, out Link? link);

	/// <summary>Looks up a link by its own identity — the lookup <see cref="RemoveLinkCommand"/> uses.</summary>
	bool TryGetById(System.Guid linkId, out Link? link);
}
