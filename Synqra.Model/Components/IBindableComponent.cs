using System;

namespace Synqra;

/// <summary>
/// Adds container-linkage to the standard <see cref="IBindableModel"/> contract for
/// types that participate as components. The generator emits an implementation of
/// this interface on every <c>[SynqraModel]</c> class that also implements
/// <see cref="IComponent"/>; the projection calls
/// <see cref="AttachToContainer"/> after applying <see cref="ComponentAddedEvent"/>
/// so the component's property setters can resolve their container's identity at
/// write time.
/// </summary>
public interface IBindableComponent : IBindableModel, IComponent
{
	/// <summary>
	/// Records the component's container linkage. Called once by the projection
	/// during event apply (live or replay). Subsequent property setters use these
	/// values to fill <see cref="ChangeComponentPropertyCommand.TargetId"/> /
	/// <see cref="ChangeComponentPropertyCommand.TargetTypeId"/> /
	/// <see cref="ChangeComponentPropertyCommand.CollectionId"/> without the user
	/// having to thread the container reference manually.
	/// </summary>
	void AttachToContainer(
		IObjectStore store,
		Guid containerId,
		Guid containerTypeId,
		Guid containerCollectionId);
}
