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

	public static IHostApplicationBuilder AddInMemorySynqraStore(this IHostApplicationBuilder builder)
	{
		builder.Services.AddSingleton<InMemoryProjection>();
		builder.Services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<InMemoryProjection>());
		builder.Services.AddSingleton<IProjection>(sp => sp.GetRequiredService<InMemoryProjection>());
		// builder.Services.AddSingleton(typeof(IStoreCollection<>), (sp, s) => sp.GetRequiredService<IStoreContext>().Get<>); // Example storage implementation
		return builder;
	}
}
