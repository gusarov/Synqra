namespace Synqra;

/// <summary>
/// Anything that can hold <see cref="IComponent"/>s. In the Quotaly substrate this
/// is the "node" concept (a stable identity with a varying set of facets).
/// <para>
/// Implementations are normally <c>[SynqraModel] partial class</c>; the generator
/// detects <see cref="IComponentContainer"/> on the class and emits the
/// <see cref="Components"/> collection wired to <c>AddComponentCommand</c> /
/// <c>DeleteComponentCommand</c> as it mutates.
/// </para>
/// </summary>
public interface IComponentContainer
{
	/// <summary>
	/// Live collection of attached components. May be empty.
	/// </summary>
	IComponentsCollection Components { get; }
}
