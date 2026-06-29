using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;

namespace Synqra.Projection.InMemory;

public static class InMemorySynqraExtensions
{
	static InMemorySynqraExtensions()
	{
		// AOT ROOTS:
		_ = typeof(IAppendStorage<Event, Guid>);
	}
	public static void AddInMemorySynqraStore(this IServiceCollection builder)
	{
		builder.AddInMemorySynqraStore<InMemoryProjection, InMemoryProjection>();
	}

	public static void AddInMemorySynqraStore<TI, T>(this IServiceCollection services)
		where TI : class, IObjectStore // it is very confusing, but it really means it is - interface! Because next line trigger multiple inheritance otherwise
		where T : InMemoryProjection, TI
	{
		services.AddSingleton<TI, T>();
		services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<T>());
		services.AddSingleton<IProjection>(sp => sp.GetRequiredService<T>());
		// builder.AddSingleton(typeof(IStoreCollection<>), (sp, s) => sp.GetRequiredService<IStoreContext>().Get<>); // Example storage implementation
		// return services;
	}

	/// <summary>
	/// Keyed variant — see <see cref="Synqra.Projection.MongoDb.MongoSynqraStoreExtensions"/>'s
	/// keyed overload doc for the full rationale. Use when a single process hosts more than one
	/// independent Synqra-backed feature: each gets its own InMemoryProjection under its own key
	/// rather than racing for the global, unkeyed IObjectStore/IProjection singleton slot.
	/// </summary>
	public static void AddInMemorySynqraStore(this IServiceCollection services, string serviceKey)
	{
		services.AddKeyedSingleton<InMemoryProjection>(serviceKey);
		services.AddKeyedSingleton<IObjectStore>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<InMemoryProjection>(key));
		services.AddKeyedSingleton<IProjection>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<InMemoryProjection>(key));
	}
}
