using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Synqra;

public static class StreamSeedingExtensions
{
	/// <summary>
	/// Registers a seeder, and the <see cref="StreamSeeding"/> that runs it, keyed by <paramref name="serviceKey"/> when given (the
	/// key of the replication endpoint and store it seeds). Register it on the master; a client registers it only to seed the
	/// streams it creates.
	/// </summary>
	public static IServiceCollection AddStreamSeeder<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSeeder>(this IServiceCollection services, string? serviceKey = null)
		where TSeeder : class, IStreamSeeder
	{
		if (serviceKey is null)
		{
			services.AddSingleton<IStreamSeeder, TSeeder>();
			services.TryAddSingleton<StreamSeeding>();
		}
		else
		{
			services.AddKeyedSingleton<IStreamSeeder, TSeeder>(serviceKey);
			services.TryAddKeyedSingleton<StreamSeeding>(serviceKey);
		}
		return services;
	}
}
