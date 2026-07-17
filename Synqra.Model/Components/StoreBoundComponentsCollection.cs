using System;
using System.Collections;
using System.Collections.Generic;

namespace Synqra;

/// <summary>
/// Generator-emitted wrapper around <see cref="ComponentsCollection"/> that routes
/// user-driven <see cref="ICollection{T}.Add"/> and <see cref="ICollection{T}.Remove"/>
/// through the Synqra command channel when the host container is attached to a store.
/// <para>
/// Same pattern as the generator-emitted property setter:
/// </para>
/// <list type="bullet">
///   <item>Pre-attach (no store) — direct mutation of the inner data structure so
///     code paths like collection initializers work before the container is added
///     to its <c>StoreCollection</c>.</item>
///   <item>Post-attach (store present) — emit <see cref="AddComponentCommand"/> /
///     <see cref="DeleteComponentCommand"/>; the projection's event-apply path
///     then mutates state via <see cref="IComponentsCollection.TryAdd"/> /
///     <see cref="IComponentsCollection.BypassRemove"/>.</item>
/// </list>
/// <para>
/// Optimistic-concurrency precondition probes the <i>container's</i> last event id
/// — components inherit their container's conflict boundary, matching the
/// per-target precondition semantics elsewhere in the substrate.
/// </para>
/// </summary>
public sealed class StoreBoundComponentsCollection : IComponentsCollection
{
	readonly ComponentsCollection _inner = new();
	readonly object _container;

	IObjectStore? _store;
	Guid _containerCollectionId;

	public StoreBoundComponentsCollection(object container)
	{
		_container = container ?? throw new ArgumentNullException(nameof(container));
	}

	/// <summary>
	/// Records the linkage to the projection. Called by the generator-emitted
	/// <see cref="IBindableModel.Attach"/> implementation on the host container.
	/// We deliberately do NOT resolve the container's id here — at the moment
	/// <see cref="IBindableModel.Attach"/> runs, the StoreCollection has not yet
	/// finished registering the host with the projection, so
	/// <c>store.GetId(container)</c> would throw. Instead we capture only the
	/// store + collection id and re-resolve the container's id on each
	/// command emit.
	/// </summary>
	public void Attach(IObjectStore store, Guid containerCollectionId)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_containerCollectionId = containerCollectionId;
	}

	public int Count => _inner.Count;
	public bool IsReadOnly => false;
	public IEnumerator<IComponent> GetEnumerator() => _inner.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	public IComponent? GetUniqueComponent(Type uniqueComponentType) => _inner.GetUniqueComponent(uniqueComponentType);
	public bool CanAddComponent(Type componentType) => _inner.CanAddComponent(componentType);
	public bool Contains(IComponent item) => _inner.Contains(item);
	public void CopyTo(IComponent[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
	public void Clear() => _inner.Clear();

	// Bypass paths — used by the projection's event-apply visitors.
	public bool TryAdd(IComponent component) => _inner.TryAdd(component);
	public bool BypassRemove(IComponent component) => _inner.BypassRemove(component);

	// User paths — go through the command channel when the container is attached.
	void ICollection<IComponent>.Add(IComponent component)
	{
		if (component is null) throw new ArgumentNullException(nameof(component));
		if (_store is null)
		{
			// Pre-attach (e.g. collection initializer in the container ctor body).
			if (!_inner.TryAdd(component))
			{
				throw new InvalidOperationException(
					$"Component '{component.GetType().Name}' cannot be added: uniqueness or veto check failed.");
			}
			return;
		}
		EmitAddCommand(component);
	}

	public bool Remove(IComponent component)
	{
		if (component is null) throw new ArgumentNullException(nameof(component));
		if (_store is null)
		{
			return _inner.BypassRemove(component);
		}
		return EmitRemoveCommand(component);
	}

	void EmitAddCommand(IComponent component)
	{
		var containerId = _store!.GetId(_container);
		var containerTypeId = _store.TypeMetadataProvider.GetTypeMetadata(_container.GetType()).TypeId;
		var componentTypeId = _store.TypeMetadataProvider.GetTypeMetadata(component.GetType()).TypeId;
		var componentId = component is IIdentifiable<Guid> id ? id.Id : Guid.Empty;
		var task = _store.SubmitCommandAsync(
			new AddComponentCommand
			{
				CollectionId = _containerCollectionId,
				TargetId = containerId,
				TargetTypeId = containerTypeId,
				ComponentTypeId = componentTypeId,
				ComponentId = componentId,
				Data = component,
			},
			new CommandSubmissionOptions { ExpectedLastEventId = _store.GetLastEventId(containerId) });
		if (!OperatingSystem.IsBrowser())
		{
			task.GetAwaiter().GetResult();
		}
	}

	bool EmitRemoveCommand(IComponent component)
	{
		var containerId = _store!.GetId(_container);
		var containerTypeId = _store.TypeMetadataProvider.GetTypeMetadata(_container.GetType()).TypeId;
		var componentTypeId = _store.TypeMetadataProvider.GetTypeMetadata(component.GetType()).TypeId;
		var componentId = component is IIdentifiable<Guid> id ? id.Id : Guid.Empty;
		var task = _store.SubmitCommandAsync(
			new DeleteComponentCommand
			{
				CollectionId = _containerCollectionId,
				TargetId = containerId,
				TargetTypeId = containerTypeId,
				ComponentTypeId = componentTypeId,
				ComponentId = componentId,
			},
			new CommandSubmissionOptions { ExpectedLastEventId = _store.GetLastEventId(containerId) });
		if (!OperatingSystem.IsBrowser())
		{
			task.GetAwaiter().GetResult();
		}
		return true;
	}
}
