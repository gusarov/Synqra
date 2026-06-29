using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Synqra;

/// <summary>
/// Blazor WebAssembly's WebAssemblyHost.RunAsync() never starts registered
/// IHostedService instances the way a normal generic Host does — there's no
/// hosted-service pump in WASM's minimal hosting model at all
/// (confirmed: dotnet/aspnetcore issue #41860, "AddHostedService&lt;T&gt;() has no
/// effect"). AddHostedService(...) registrations (e.g. EventReplicationService)
/// silently never run unless something explicitly starts them. Call this once,
/// right after WebAssemblyHostBuilder.Build(), before RunAsync().
/// </summary>
public static class WasmHostedServiceExtensions
{
	public static async Task StartHostedServicesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
	{
		foreach (var hostedService in services.GetServices<IHostedService>())
		{
			await hostedService.StartAsync(cancellationToken);
		}
	}
}
