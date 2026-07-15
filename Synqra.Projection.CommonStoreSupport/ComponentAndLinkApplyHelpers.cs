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
	/// (ComponentTypeId, ComponentId). Every component has a first-class id — unique ones too, since
	/// uniqueness is a max-cardinality constraint, not identity suppression — so a component is always
	/// addressed strictly by <see cref="ComponentId"/> (walk the list, match by identity).
	/// </summary>
	public static IComponent ResolveComponent(IComponentContainer container, SingleObjectEvent ev, ITypeMetadataProvider typeMetadataProvider)
	{
		var componentType = typeMetadataProvider.GetTypeMetadata(GetComponentTypeId(ev)).Type;
		var componentId = GetComponentId(ev);

		foreach (var c in container.Components)
		{
			if (c is IIdentifiable<Guid> identifiable && identifiable.Id == componentId)
			{
				return c;
			}
		}
		throw new InvalidOperationException($"Component {componentId} of type '{componentType.Name}' not found on container.");
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
	/// <para>
	/// <paramref name="componentId"/> is the event's authoritative id, stamped onto the materialized
	/// instance so a replayed/rehydrated component keeps its persisted id (not the fresh v7 its
	/// constructor auto-assigns). Every component has a first-class id — unique ones too.
	/// </para>
	/// </summary>
	public static IComponent MaterializeComponent(Type componentType, object? data, Guid componentId)
	{
		IComponent component;
		if (data is IComponent ready && componentType.IsInstanceOfType(ready))
		{
			// Live instance emitted locally — already carries the right id.
			component = ready;
		}
		else
		{
			var instance = Activator.CreateInstance(componentType)
				?? throw new InvalidOperationException($"Could not instantiate component '{componentType.Name}'.");
			component = (IComponent)instance;

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
		}

		// Single exit: every construction path funnels through this one id-stamp, so a rehydrated
		// instance always keeps its persisted id (over the fresh v7 its constructor auto-assigns) —
		// there is no way to materialize a component here and skip it.
		if (component is IBindableComponent bindableComponent && componentId != Guid.Empty)
		{
			bindableComponent.SetComponentId(componentId);
		}
		return component;
	}
}

/// <summary>Pure materialization logic shared by every projection's link event-apply path.</summary>
public static class LinkApplyHelpers
{
	/// <summary>
	/// Guards every projection's <c>GetCollection</c>/<c>GetCollection&lt;T&gt;</c> against
	/// <see cref="Link"/>-derived types. Links never live in the generic per-type collection those
	/// methods build — they have their own dedicated index (<see cref="ILinkIndex"/>) maintained from
	/// <see cref="LinkAddedEvent"/>/<see cref="LinkRemovedEvent"/> (see plans/links.md). Without this
	/// guard, <c>GetCollection&lt;TLink&gt;()</c> compiles fine and silently returns an always-empty
	/// collection, since nothing ever populates it for a link type.
	/// </summary>
	public static void GuardNotLinkType(Type type)
	{
		if (typeof(Link).IsAssignableFrom(type))
		{
			throw new NotSupportedException(
				$"GetCollection<{type.Name}>() cannot be used for '{type.Name}' — it derives from Link, and links are not stored in the generic object collection. " +
				$"Use ILinkIndex instead (cast the store to ILinkIndex and read Links / LinksAt / LinksBetween), or the generated [To]/[From]/[Related] navigation properties.");
		}
	}

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
