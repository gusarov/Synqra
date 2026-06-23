using System;

namespace Synqra;

/// <summary>
/// Shared component-activation seam used by every projection (in-memory, MongoDB, …) so the
/// construct + hydrate + activate logic — and the curated DI surface handed to activation — lives in
/// one place instead of being duplicated per projection.
/// <para>
/// The projection still owns container/collection wiring (adding the component to the container,
/// attaching it to the store); this only covers <b>creating</b> the component and running its
/// <see cref="IActivatableComponent.Activate"/> hook against a controlled service provider.
/// </para>
/// </summary>
public interface ISynqraComponentActivator
{
	/// <summary>
	/// Create the component instance: construct it through DI (so it can take constructor
	/// dependencies from the curated activation provider) and hydrate stored data onto it. If
	/// <paramref name="data"/> is already the live component instance, it is returned as-is.
	/// </summary>
	IComponent Materialize(Type componentType, object? data);

	/// <summary>
	/// Run the component's <see cref="IActivatableComponent.Activate"/> hook (when it implements it and
	/// this is not a replay), passing the curated activation service provider.
	/// </summary>
	void Activate(IComponent component, IComponentContainer container, Guid containerId, bool isReplay);
}
