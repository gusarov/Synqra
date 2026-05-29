using System;
using System.Collections.Generic;

namespace Synqra;

/// <summary>
/// Collection of <see cref="IComponent"/>s on an <see cref="IComponentContainer"/>.
/// Enforces uniqueness constraints declared via <c>[Component(IsUnique = true)]</c>
/// at the type or implemented-interface level, and runs <see cref="ICanAddComponent"/>
/// vetoes from existing members.
/// <para>
/// Direct collection mutations are an implementation detail of the projection's
/// event-apply path; user code reaches components through the container's
/// generated <c>Components</c> property, whose mutations go through the
/// command channel.
/// </para>
/// </summary>
public interface IComponentsCollection : ICollection<IComponent>
{
	/// <summary>
	/// Looks up the (at most one) component instance whose type satisfies the
	/// given <c>[Component(IsUnique = true)]</c> interface or class. Returns
	/// <c>null</c> when no unique slot is filled.
	/// </summary>
	IComponent? GetUniqueComponent(Type uniqueComponentType);

	/// <summary>
	/// Whether a component of the given type could be added right now —
	/// passes both the uniqueness check and any <see cref="ICanAddComponent"/>
	/// vetoes by existing members.
	/// </summary>
	bool CanAddComponent(Type componentType);

	/// <summary>
	/// Attempts to add a component. Returns <c>false</c> if uniqueness or veto
	/// checks reject it — used by the projection's event-apply path to
	/// distinguish acceptance from rejection without throwing.
	/// (<see cref="ICollection{T}.Add"/> returns <c>void</c>; this variant exposes
	/// the success bit.)
	/// </summary>
	bool TryAdd(IComponent component);
}
