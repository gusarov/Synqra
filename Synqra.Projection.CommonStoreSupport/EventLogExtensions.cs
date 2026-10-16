using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;

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

	// PublicConstructors flows to ActivatorUtilities.CreateInstance<TLog>: without it the trimmer drops the log's constructor (native AOT).
	static void AddEventLog<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLog>(this IServiceCollection services, string? serviceKey)
		where TLog : EventLog
	{
		services.AddOptions<EventSequencerOptions>();
		if (serviceKey is null)
		{
			services.AddSingleton<EventSequencer>();
			services.AddSingleton<IEventLog>(sp => ActivatorUtilities.CreateInstance<TLog>(sp
				, sp.GetRequiredService<IAppendStorage<Event, (Guid Stream, Guid Sequence)>>()
				, sp.GetRequiredService<EventSequencer>()
			));
		}
		else
		{
			services.AddKeyedSingleton<EventSequencer>(serviceKey);
			services.AddKeyedSingleton<IEventLog>(serviceKey, (sp, key) => ActivatorUtilities.CreateInstance<TLog>(sp
				, sp.GetRequiredKeyedService<IAppendStorage<Event, (Guid Stream, Guid Sequence)>>(key)
				, sp.GetRequiredKeyedService<EventSequencer>(key)
			));
		}
	}
}
