namespace Synqra.Projection;

/// <summary>
/// Applies a canonical <see cref="ObjectData"/> property bag onto a freshly-created instance — the
/// one hydration routine every "materialize from replay" path (object, component, link) shares.
/// Uses the bindable-model <c>Set</c> path for SynqraModel types (no reflection); falls back to
/// reflection for plain POCOs, converting <see cref="IConvertible"/> values to each target
/// property's declared type.
/// </summary>
public static class ObjectDataApplyHelpers
{
	public static void HydrateFromData(object instance, Type type, ObjectData data)
	{
		if (instance is IBindableModel bindable)
		{
			foreach (var (key, value) in data)
			{
				bindable.Set(key, value);
			}
		}
		else
		{
			foreach (var (key, value) in data)
			{
				var pi = type.GetProperty(key);
				if (pi is null) continue;
				var v = value;
				if (v is IConvertible c)
				{
					v = c.ToType(pi.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
				}
				pi.SetValue(instance, v);
			}
		}
	}
}

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
	/// Two cases: <paramref name="liveInstance"/> is already an instance of <paramref name="componentType"/>
	/// (the in-process create path — the caller's own component must come back, not a copy, since the
	/// generated property setters on whatever <see cref="AttachToContainer"/>-equivalent wiring runs next
	/// route subsequent writes through it; a fresh copy would leave the caller's reference permanently
	/// detached from the store); or it's absent (replay/cross-process), so a fresh instance is constructed
	/// and hydrated from <paramref name="data"/>, the canonical property bag.
	/// </summary>
	public static IComponent MaterializeComponent(Type componentType, object? liveInstance, ObjectData data)
	{
		if (liveInstance is IComponent ready && componentType.IsInstanceOfType(ready))
		{
			return ready;
		}

		var instance = Activator.CreateInstance(componentType)
			?? throw new InvalidOperationException($"Could not instantiate component '{componentType.Name}'.");
		var component = (IComponent)instance;

		ObjectDataApplyHelpers.HydrateFromData(component, componentType, data);
		return component;
	}
}

/// <summary>Pure materialization logic shared by every projection's link event-apply path.</summary>
public static class LinkApplyHelpers
{
	/// <summary>
	/// Always constructs a fresh instance and hydrates it from <paramref name="data"/> — no "reuse the
	/// live instance" fast path, unlike <see cref="ComponentApplyHelpers.MaterializeComponent"/>, because
	/// nothing downstream of link creation relies on reference identity: every nav-collection read
	/// re-queries the store's <c>ILinkIndex</c> rather than caching the caller's submitted instance, and
	/// links have no post-creation property-change command. <c>LinkId</c>/<c>SourceId</c>/<c>TargetId</c>
	/// are re-stamped from the event's own explicit fields by the caller regardless of what
	/// <paramref name="data"/> carries (see <c>LinkAddedEvent</c>'s remarks).
	/// </summary>
	public static Link MaterializeLink(Type linkType, ObjectData data)
	{
		var instance = (Link)(Activator.CreateInstance(linkType)
			?? throw new InvalidOperationException($"Could not instantiate link '{linkType.Name}'."));

		ObjectDataApplyHelpers.HydrateFromData(instance, linkType, data);
		return instance;
	}
}
