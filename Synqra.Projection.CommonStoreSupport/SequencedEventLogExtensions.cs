using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;

namespace Synqra;

public static class SequencedEventLogExtensions
{
	/// <summary>
	/// Makes the registered <see cref="IAppendStorage{Event, Guid}"/> (keyed by <paramref name="serviceKey"/> when given) the
	/// server's sequenced log: every append is stamped with its commit order and reported through
	/// <see cref="ICommittedEventSource"/>. A server host calls this once after registering its log; a client never does.
	/// Tune the sequencer with <c>services.Configure&lt;EventSequencerOptions&gt;(...)</c>.
	/// </summary>
	public static IServiceCollection AddSequencedEventLog(this IServiceCollection services, string? serviceKey = null)
	{
		var result = services;
		if (services.Any(d => d.ServiceType == typeof(EventSequencer) && Equals(d.ServiceKey, serviceKey)))
		{
			result = services; // already the sequenced log
		}
		else
		{
			result = services.Decorate(serviceKey);
		}
		return result;
	}

	static IServiceCollection Decorate(this IServiceCollection services, string? serviceKey)
	{
		var index = services.ToList().FindLastIndex(d => d.ServiceType == typeof(IAppendStorage<Event, Guid>) && Equals(d.ServiceKey, serviceKey));
		if (index < 0)
		{
			throw new InvalidOperationException($"AddSequencedEventLog: no IAppendStorage<Event, Guid> is registered{(serviceKey is null ? "" : $" under key '{serviceKey}'")}; register the log first.");
		}
		var inner = services[index];
		services.AddOptions<EventSequencerOptions>();
		ServiceDescriptor sequenced;
		if (serviceKey is null)
		{
			services.AddSingleton<EventSequencer>();
			sequenced = ServiceDescriptor.Singleton<IAppendStorage<Event, Guid>>(sp => new SequencedEventLog(inner.Instantiate(sp, null), sp.GetRequiredService<EventSequencer>()));
		}
		else
		{
			services.AddKeyedSingleton<EventSequencer>(serviceKey);
			sequenced = ServiceDescriptor.KeyedSingleton<IAppendStorage<Event, Guid>>(serviceKey, (sp, key) => new SequencedEventLog(inner.Instantiate(sp, key), sp.GetRequiredKeyedService<EventSequencer>(key)));
		}
		services[index] = sequenced;
		return services;
	}

	static IAppendStorage<Event, Guid> Instantiate(this ServiceDescriptor inner, IServiceProvider sp, object? key)
	{
		object instance;
		if (inner.IsKeyedService)
		{
			instance = inner.KeyedImplementationInstance
				?? inner.KeyedImplementationFactory?.Invoke(sp, key)
				?? ActivatorUtilities.CreateInstance(sp, inner.KeyedImplementationType!);
		}
		else
		{
			instance = inner.ImplementationInstance
				?? inner.ImplementationFactory?.Invoke(sp)
				?? ActivatorUtilities.CreateInstance(sp, inner.ImplementationType!);
		}
		return (IAppendStorage<Event, Guid>)instance;
	}
}
