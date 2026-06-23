namespace Synqra;

/// <summary>
/// Marker for component implementations. A component is a typed chunk of data and
/// (optionally) behaviour attached to an <see cref="IComponentContainer"/>.
/// <para>
/// Implementing types are normally declared as <c>[SynqraModel] partial class</c>
/// so the generator can produce the bindable setter/event plumbing. The generator
/// detects <see cref="IComponent"/> on the class and emits
/// <c>ChangeComponentPropertyCommand</c> from property setters instead of the
/// node-level <c>ChangeObjectPropertyCommand</c>.
/// </para>
/// </summary>
public interface IComponent
{
}

/// <summary>
/// Existing components can veto an incoming attach. Useful when one component
/// implies invariants about its peers (e.g. an OS component refusing a peer of
/// a contradictory OS).
/// </summary>
public interface ICanAddComponent
{
	bool CanAddAnotherComponent(IComponentsCollection components, System.Type incomingComponentType);
}
