using System;

namespace Synqra;

/// <summary>
/// Declares constraints for a component type or component interface.
/// <para>
/// Applied to the implementing class OR to any interface in its hierarchy.
/// The <see cref="IsUnique"/> flag is the typical use:
/// </para>
/// <para>
/// <c>[Component(IsUnique = true)] public interface IOsComponent : IComponent { }</c>
/// — at most one component implementing <c>IOsComponent</c> per container,
/// regardless of which concrete class fills the slot (LinuxOs, WindowsOs, …).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, Inherited = true, AllowMultiple = false)]
public sealed class ComponentAttribute : Attribute
{
	/// <summary>
	/// At most one component satisfying this declaration per container. A unique
	/// component type therefore has no individual identity (no <c>ComponentId</c>);
	/// the (container, unique-type) pair addresses it.
	/// </summary>
	public bool IsUnique { get; set; }
}
