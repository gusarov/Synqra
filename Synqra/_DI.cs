using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Synqra;

public static class SynqraExtensions
{
	static SynqraExtensions()
	{
		// AOT ROOTS:
		_ = typeof(IStorage<Event, Guid>);
	}

	public static IHostApplicationBuilder AddSynqraStoreContext(this IHostApplicationBuilder builder)
	{
		// builder.Services.AddSingleton<StoreContext>();
		// builder.Services.AddSingleton<IStoreContext>(sp => sp.GetRequiredService<StoreContext>());
		builder.Services.AddSingleton<ISynqraStoreContext, StoreContext>();
		// builder.Services.AddSingleton(typeof(IStoreCollection<>), (sp, s) => sp.GetRequiredService<IStoreContext>().Get<>); // Example storage implementation
		return builder;
	}
}
