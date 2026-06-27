namespace Synqra;

/// <summary>
/// Which end of a link a navigation property sits at — i.e. the role the declaring node plays.
/// </summary>
public enum LinkEnd
{
	/// <summary>The declaring node is the link's source; navigation yields the targets.</summary>
	Source,

	/// <summary>The declaring node is the link's target; navigation yields the sources.</summary>
	Target,

	/// <summary>Undirected: the declaring node is either end; navigation yields the opposite end.</summary>
	Either,
}
