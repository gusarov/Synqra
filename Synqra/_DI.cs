using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synqra.AppendStorage;
using Synqra.BlobStorage;

namespace Synqra;

public static class ClientReplicationServiceCollectionExtensions
{
	public static IServiceCollection AddReplicationProtocol(this IServiceCollection services)
	{
		services.TryAddSingleton<ReplicationRecordCodec>();
		services.TryAddSingleton<ReplicationProtocol>();
		return services;
	}

	public static IServiceCollection AddClientEventStoreBlob(
		  this IServiceCollection services
		, string confirmedStoreName
		, string pendingStoreName
	)
	{
		services.AddReplicationProtocol();
		services.TryAddSingleton(sp => new BlobClientEventStore(
			  sp.GetRequiredKeyedService<IBlobStorage<Guid>>(confirmedStoreName)
			, sp.GetRequiredKeyedService<IBlobStorage<Guid>>(pendingStoreName)
			, sp.GetRequiredService<ReplicationRecordCodec>()
		));
		services.TryAddSingleton<IClientEventStore>(sp => sp.GetRequiredService<BlobClientEventStore>());
		services.TryAddSingleton<IAppendStorage<Event, Guid>>(sp => sp.GetRequiredService<BlobClientEventStore>());
		return services;
	}
}
