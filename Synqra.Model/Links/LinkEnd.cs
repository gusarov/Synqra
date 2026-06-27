namespace Synqra;

/// <summary>
/// Which end of a link a navigation property sits at — i.e. the role the declaring node plays.
/// </summary>
public enum LinkEnd
{
	/// <summary>
	/// Not set. <c>default(LinkEnd)</c> deliberately lands here, not on a real value — every
	/// navigation collection and <see cref="ILinkIndex"/> query validates this explicitly and
	/// throws rather than silently falling back to some other end's behaviour. An uninitialized
	/// <see cref="LinkEnd"/> reaching any of those is always a bug, never a legitimate "don't care".
	/// </summary>
	None,

	/// <summary>The declaring node is the link's source; navigation yields the targets.</summary>
	Source,

	/// <summary>The declaring node is the link's target; navigation yields the sources.</summary>
	Target,

	/// <summary>Undirected: the declaring node is either end; navigation yields the opposite end.</summary>
	Either,
}
