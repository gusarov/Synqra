namespace Synqra;

/// <summary>
/// Implemented explicitly by the generator on any model that declares <c>[To]</c>/<c>[From]</c>/
/// <c>[Related]</c> navigation properties. The store calls this on both endpoints whenever a link
/// touching them is added or removed.
/// <para>
/// Navigation properties like <c>Parent</c>/<c>Children</c> are live queries with no backing field —
/// they always return the correct answer on access, by construction (see plans/links.md), but
/// nothing about "the answer changed" is otherwise observable. This is the hook that lets the
/// generated implementation translate "a link of this type changed at this end" into the right
/// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> notifications for its
/// own declared nav properties.
/// </para>
/// </summary>
public interface ILinkAware
{
	/// <summary>
	/// A link of <paramref name="linkType"/> was added or removed, with this object playing the role
	/// given by <paramref name="selfEnd"/> (<see cref="LinkEnd.Source"/> or <see cref="LinkEnd.Target"/>
	/// — never <see cref="LinkEnd.Either"/>; an undirected link still has a concrete stored side, the
	/// implementation decides which of its own <c>[Related]</c> properties that maps to).
	/// </summary>
	void OnLinkChanged(System.Type linkType, LinkEnd selfEnd);
}
