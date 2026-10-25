using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Synqra;

public static class InboundEventGuardExtensions
{
	/// <summary>
	/// Registers a guard the replication endpoint asks before applying a peer's event, keyed by <paramref name="serviceKey"/> when given
	/// (the endpoint's key). Several may be registered; the first that refuses decides.
	/// </summary>
	public static IServiceCollection AddInboundEventGuard<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TGuard>(this IServiceCollection services, string? serviceKey = null)
		where TGuard : class, IInboundEventGuard
	{
		if (serviceKey is null)
		{
			services.AddSingleton<IInboundEventGuard, TGuard>();
		}
		else
		{
			services.AddKeyedSingleton<IInboundEventGuard, TGuard>(serviceKey);
		}
		return services;
	}
}
