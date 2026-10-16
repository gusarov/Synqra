using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Synqra;

public static class EventLogExtensions
{
	/// <summary>
	/// The server's <see cref="IEventLog"/> (commit order) over the registered <c>IAppendStorage&lt;Event, (Guid, Guid)&gt;</c>,
	/// keyed by <paramref name="serviceKey"/> when given. The storage stays registered as itself; consumers ask for
	/// <see cref="IEventLog"/>. Tune the sequencer with <c>services.Configure&lt;EventSequencerOptions&gt;(...)</c>.
	/// </summary>
	public static IServiceCollection AddServerEventLog(this IServiceCollection services, string? serviceKey = null)
	{
		services.AddEventLog<ServerEventLog>(serviceKey);
		return services;
	}

	/// <summary>A client's local <see cref="IEventLog"/> (arrival order) over the registered storage; see <see cref="AddServerEventLog"/>.</summary>
	public static IServiceCollection AddClientEventLog(this IServiceCollection services, string? serviceKey = null)
	{
		services.AddEventLog<ClientEventLog>(serviceKey);
		return services;
	}

	// The log's dependencies inherit its key ([FromKeyedServices]); PublicConstructors keeps its constructor through the trimmer (native AOT).
	static void AddEventLog<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLog>(this IServiceCollection services, string? serviceKey)
		where TLog : EventLog
	{
		services.AddOptions<EventSequencerOptions>();
		if (serviceKey is null)
		{
			services.TryAddSingleton<EventSequencer>();
			services.TryAddSingleton<IEventLog, TLog>();
		}
		else
		{
			services.TryAddKeyedSingleton<EventSequencer>(serviceKey);
			services.TryAddKeyedSingleton<IEventLog, TLog>(serviceKey);
		}
	}
}
