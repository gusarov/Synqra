using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Synqra;

/// <summary>
/// <see cref="EventReplicationService"/> depends on <c>Lazy&lt;IProjection&gt;</c> (deferred
/// resolution — the projection and the replication service can otherwise form a
/// constructor-time circular dependency), but the built-in DI container has no native
/// support for resolving an open generic <c>Lazy&lt;&gt;</c>. Any real host that wants to
/// register EventReplicationService needs this. Previously only existed as a test-only
/// helper (Synqra.Tests.TestHelpers/BaseTest.cs's Lazier&lt;T&gt;) — promoted here since it's
/// genuinely required by production code, not just tests.
/// </summary>
public static class LazyServiceResolutionExtensions
{
	public static IServiceCollection AddLazyServiceResolution(this IServiceCollection services)
	{
		services.TryAddTransient(typeof(Lazy<>), typeof(LazyServiceResolution<>));
		return services;
	}
}

sealed class LazyServiceResolution<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T> : Lazy<T>
	where T : class
{
	public LazyServiceResolution(IServiceProvider serviceProvider)
		: base(() => serviceProvider.GetRequiredService<T>())
	{
	}
}
