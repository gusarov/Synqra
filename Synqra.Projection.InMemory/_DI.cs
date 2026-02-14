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

	public static IHostApplicationBuilder AddSynqraStoreContext(this IHostApplicationBuilder builder)
	{
		// builder.Services.AddSingleton<StoreContext>();
		// builder.Services.AddSingleton<IStoreContext>(sp => sp.GetRequiredService<StoreContext>());
		builder.Services.AddSingleton<IProjection, InMemoryProjection>();
		// builder.Services.AddSingleton(typeof(IStoreCollection<>), (sp, s) => sp.GetRequiredService<IStoreContext>().Get<>); // Example storage implementation
		return builder;
	}
}
