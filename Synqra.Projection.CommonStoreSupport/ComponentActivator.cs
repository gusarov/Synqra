using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synqra;

namespace Synqra.Projection;

/// <summary>
/// A deliberately tiny, controlled service provider for component activation: it resolves ONLY the
/// services explicitly placed in it (none today — components that need to know the runtime use
/// <see cref="System.OperatingSystem.IsBrowser"/> directly). This keeps activation off the host's full
/// container; grow the curated set here when a real activation dependency appears.
/// </summary>
sealed class CuratedActivationServiceProvider : IServiceProvider
{
	readonly IReadOnlyDictionary<Type, object> _services;
	public CuratedActivationServiceProvider(IReadOnlyDictionary<Type, object> services) => _services = services;
	public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var service) ? service : null;
}

/// <summary>
/// Default <see cref="ISynqraComponentActivator"/>: constructs components through DI against the curated
/// activation provider (so they can take constructor dependencies), hydrates stored data onto them, and
/// runs their <see cref="IActivatableComponent.Activate"/> hook off-replay with that same provider.
/// </summary>
public sealed class DefaultComponentActivator : ISynqraComponentActivator
{
	readonly IServiceProvider _activationServices;

	public DefaultComponentActivator(IServiceProvider activationServices)
		=> _activationServices = activationServices ?? throw new ArgumentNullException(nameof(activationServices));

	public IComponent Materialize(Type componentType, object? data)
	{
		// Already the live instance (emitted locally) — use as-is.
		if (data is IComponent ready && componentType.IsInstanceOfType(ready))
		{
			return ready;
		}

		var component = (IComponent)ActivatorUtilities.CreateInstance(_activationServices, componentType);

		// Hydrate from a JSON-shaped dictionary (rehydrated from the event store / read-model).
		if (data is IDictionary<string, object?> bag)
		{
			if (component is IBindableModel bindable)
			{
				foreach (var (key, value) in bag)
				{
					bindable.Set(key, value);
				}
			}
			else
			{
				foreach (var (key, value) in bag)
				{
					var pi = componentType.GetProperty(key);
					if (pi is null)
					{
						continue;
					}
					var v = value;
					if (v is IConvertible c)
					{
						v = c.ToType(pi.PropertyType, CultureInfo.InvariantCulture);
					}
					pi.SetValue(component, v);
				}
			}
		}
		return component;
	}

	public void Activate(IComponent component, IComponentContainer container, Guid containerId, bool isReplay)
	{
		if (isReplay || component is not IActivatableComponent activatable)
		{
			return;
		}
		activatable.Activate(new ComponentActivationContext
		{
			Container = container,
			ContainerId = containerId,
			Component = component,
			IsReplay = false,
		});
	}
}

public static class SynqraComponentActivatorExtensions
{
	/// <summary>
	/// Register the shared <see cref="ISynqraComponentActivator"/> with a curated (currently empty)
	/// activation surface. Called by each store's DI setup.
	/// </summary>
	public static IServiceCollection AddSynqraComponentActivator(this IServiceCollection services)
	{
		services.TryAddSingleton<ISynqraComponentActivator>(_ =>
			new DefaultComponentActivator(new CuratedActivationServiceProvider(new Dictionary<Type, object>())));
		return services;
	}
}
