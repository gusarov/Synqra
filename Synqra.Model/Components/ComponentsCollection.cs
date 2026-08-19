using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Synqra;

/// <summary>
/// Default in-memory <see cref="IComponentsCollection"/>. Backed by a list plus
/// a unique-slot dictionary so <see cref="GetUniqueComponent"/> is O(1).
/// <para>
/// This is the data structure event-apply uses; user code never instantiates it
/// directly — the generator emits a <c>Components</c> property on every
/// <see cref="IComponentContainer"/> that exposes one of these.
/// </para>
/// </summary>
public sealed class ComponentsCollection : IComponentsCollection
{
	// Both structures are concurrent, so every read — enumerate, GetUniqueComponent, Contains, Count —
	// is lock-free and copies nothing. Components are attached by event-apply on a background thread
	// while request threads read the same node, and insertion order is preserved because callers do
	// reason about the order components were attached in.
	readonly ConcurrentAppendList<IComponent> _components = new ConcurrentAppendList<IComponent>();
	readonly ConcurrentDictionary<Type, IComponent> _unique = new();

	// Writers only. The uniqueness check and the mutation that follows it must be one step, and a
	// component can fill several unique slots at once, so the all-or-nothing claim of that slot set
	// cannot be expressed as a single ConcurrentDictionary operation. Readers never take this.
	readonly object _writeGate = new object();

	public int Count => _components.Count;
	public bool IsReadOnly => false;

	public IEnumerator<IComponent> GetEnumerator() => _components.GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	public IComponent? GetUniqueComponent(Type uniqueComponentType)
	{
		_unique.TryGetValue(uniqueComponentType, out var c);
		return c;
	}

	public bool CanAddComponent(Type componentType)
		=> CanAddCore(componentType, out _);

	void ICollection<IComponent>.Add(IComponent component)
	{
		if (!TryAdd(component))
		{
			throw new InvalidOperationException(
				$"Component '{component.GetType().Name}' cannot be added: uniqueness or veto check failed.");
		}
	}

	/// <summary>Adds a component if uniqueness + veto checks pass. Returns false if rejected.</summary>
	public bool TryAdd(IComponent component)
	{
		if (component is null) throw new ArgumentNullException(nameof(component));
		lock (_writeGate)
		{
			if (!CanAddCore(component.GetType(), out var uniqueSlots)) return false;
			foreach (var slot in uniqueSlots) _unique[slot] = component;
			_components.Add(component);
			return true;
		}
	}

	// Base ComponentsCollection has no command channel — both Remove and
	// BypassRemove just mutate the inner data. StoreBoundComponentsCollection
	// wraps this and overrides ICollection<T>.Remove to emit a command.
	public bool Remove(IComponent component) => BypassRemove(component);

	public bool BypassRemove(IComponent component)
	{
		lock (_writeGate)
		{
			if (!_components.Remove(component)) return false;
			foreach (var slot in EnumerateUniqueSlots(component.GetType()))
			{
				if (_unique.TryGetValue(slot, out var existing) && existing == component)
				{
					_unique.TryRemove(slot, out _);
				}
			}
			return true;
		}
	}

	// The Clear / Contains / CopyTo members of ICollection<T> are intentionally
	// blocked. Bulk reset is incompatible with the event-sourced apply model —
	// every component attach/detach should produce its own event.
	public void Clear()
		=> throw new NotSupportedException("Clear breaks event sourcing; remove components individually.");
	public bool Contains(IComponent item)
		=> _components.Contains(item);
	public void CopyTo(IComponent[] array, int arrayIndex)
		=> _components.CopyTo(array, arrayIndex);

	bool CanAddCore(Type componentType, out IReadOnlyList<Type> uniqueSlots)
	{
		var slots = EnumerateUniqueSlots(componentType).ToList();
		uniqueSlots = slots;

		// Uniqueness check: no unique slot already filled.
		foreach (var slot in slots)
		{
			if (_unique.ContainsKey(slot)) return false;
		}

		// Veto check: any existing component allowed to refuse this incoming type.
		foreach (var existing in _components)
		{
			if (existing is ICanAddComponent guard
				&& !guard.CanAddAnotherComponent(this, componentType))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Returns every type in the component's hierarchy carrying
	/// <c>[Component(IsUnique = true)]</c>. Walks the class chain and all
	/// implemented interfaces.
	/// </summary>
	static IEnumerable<Type> EnumerateUniqueSlots(Type componentType)
	{
		var seen = new HashSet<Type>();
		var slots = new List<Type>();
		Walk(componentType, seen, slots);
		return slots;
	}

	static void Walk(Type t, HashSet<Type> seen, List<Type> slots)
	{
		if (!seen.Add(t)) return;
		var attr = t.GetCustomAttribute<ComponentAttribute>(inherit: false);
		if (attr is { IsUnique: true }) slots.Add(t);
		if (t.BaseType is { } baseType && baseType != typeof(object)) Walk(baseType, seen, slots);
		foreach (var i in t.GetInterfaces()) Walk(i, seen, slots);
	}
}
