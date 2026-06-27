namespace Synqra.Projection;

/// <summary>
/// Pure materialization/addressing logic shared by every projection's component event-apply path
/// (<see cref="InMemory.InMemoryProjection"/>, <see cref="MongoDb.MongoProjection"/> — referenced by
/// namespace in this doc comment only; this project doesn't depend on either). Each projection still
/// owns its own object-tracking lookup (in-memory's live model graph vs. Mongo's per-process tracked-id
/// map), so <see cref="ResolveContainer"/> takes the already-resolved candidate rather than doing the
/// lookup itself.
/// </summary>
public static class ComponentApplyHelpers
{
	public static IComponentContainer ResolveContainer(object? model, Guid targetId)
	{
		if (model is null)
		{
			throw new InvalidOperationException($"Container {targetId} not found while applying component event.");
		}
		if (model is not IComponentContainer container)
		{
			throw new InvalidOperationException($"Object {targetId} is a '{model.GetType().Name}' which does not implement IComponentContainer.");
		}
		return container;
	}

	/// <summary>
	/// Both <see cref="ComponentPropertyChangedEvent"/> and <see cref="ComponentDeletedEvent"/> carry
	/// (ComponentTypeId, ComponentId). Addresses by ComponentId when set (non-unique components, walk
	/// the list to find by identity), or by ComponentTypeId alone (unique components, look up the slot).
	/// </summary>
	public static IComponent ResolveComponent(IComponentContainer container, SingleObjectEvent ev, ITypeMetadataProvider typeMetadataProvider)
	{
		var componentType = typeMetadataProvider.GetTypeMetadata(GetComponentTypeId(ev)).Type;
		var componentId = GetComponentId(ev);

		if (componentId != Guid.Empty)
		{
			foreach (var c in container.Components)
			{
				if (c is IIdentifiable<Guid> identifiable && identifiable.Id == componentId)
				{
					return c;
				}
			}
			throw new InvalidOperationException($"Component {componentId} of type '{componentType.Name}' not found on container.");
		}

		var unique = container.Components.GetUniqueComponent(componentType);
		if (unique is null)
		{
			throw new InvalidOperationException($"No unique-component slot for '{componentType.Name}' is filled on this container.");
		}
		return unique;
	}

	public static Guid GetComponentTypeId(SingleObjectEvent ev) => ev switch
	{
		ComponentPropertyChangedEvent p => p.ComponentTypeId,
		ComponentDeletedEvent d => d.ComponentTypeId,
		_ => throw new InvalidOperationException($"Unsupported component event type: {ev.GetType().Name}"),
	};

	public static Guid GetComponentId(SingleObjectEvent ev) => ev switch
	{
		ComponentPropertyChangedEvent p => p.ComponentId,
		ComponentDeletedEvent d => d.ComponentId,
		_ => throw new InvalidOperationException($"Unsupported component event type: {ev.GetType().Name}"),
	};

	/// <summary>
	/// Three cases: <paramref name="data"/> is already an instance of <paramref name="componentType"/>
	/// (typical when emitted locally); a json-shaped <see cref="IDictionary{TKey, TValue}"/>
	/// (post-rehydrate from the event store); or null (component has no payload, e.g. a bare marker).
	/// </summary>
	public static IComponent MaterializeComponent(Type componentType, object? data)
	{
		if (data is IComponent ready && componentType.IsInstanceOfType(ready))
		{
			return ready;
		}

		var instance = Activator.CreateInstance(componentType)
			?? throw new InvalidOperationException($"Could not instantiate component '{componentType.Name}'.");
		var component = (IComponent)instance;

		// Hydrate from dictionary via the bindable-model set path when the component is a
		// SynqraModel; fall back to reflection otherwise.
		if (data is IDictionary<string, object?> bag && component is IBindableModel bindable)
		{
			foreach (var (key, value) in bag)
			{
				bindable.Set(key, value);
			}
		}
		else if (data is IDictionary<string, object?> reflectBag)
		{
			foreach (var (key, value) in reflectBag)
			{
				var pi = componentType.GetProperty(key);
				if (pi is null) continue;
				var v = value;
				if (v is IConvertible c)
				{
					v = c.ToType(pi.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
				}
				pi.SetValue(component, v);
			}
		}
		return component;
	}
}

/// <summary>Pure materialization logic shared by every projection's link event-apply path.</summary>
public static class LinkApplyHelpers
{
	/// <summary>Same three-case materialization <see cref="ComponentApplyHelpers.MaterializeComponent"/> uses: live instance, json-shaped dict, or fresh instance with no payload.</summary>
	public static Link MaterializeLink(Type linkType, object? data)
	{
		if (data is Link ready && linkType.IsInstanceOfType(ready))
		{
			return ready;
		}

		var instance = (Link)(Activator.CreateInstance(linkType)
			?? throw new InvalidOperationException($"Could not instantiate link '{linkType.Name}'."));

		if (data is IDictionary<string, object?> bag && instance is IBindableModel bindable)
		{
			foreach (var (key, value) in bag)
			{
				bindable.Set(key, value);
			}
		}
		else if (data is IDictionary<string, object?> reflectBag)
		{
			foreach (var (key, value) in reflectBag)
			{
				var pi = linkType.GetProperty(key);
				if (pi is null) continue;
				var v = value;
				if (v is IConvertible c)
				{
					v = c.ToType(pi.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
				}
				pi.SetValue(instance, v);
			}
		}
		return instance;
	}
}
