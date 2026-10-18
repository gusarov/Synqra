using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Synqra.AppendStorage;
using Synqra.AppendStorage.MongoDb;

namespace Synqra.Projection.MongoDb;

public static class MongoSynqraStoreExtensions
{
	static MongoSynqraStoreExtensions()
	{
		// AOT ROOTS:
		_ = typeof(IAppendStorage<Event, (Guid, Guid)>);
	}

	/// <summary>
	/// A MongoDB-backed Synqra store (<see cref="IObjectStore"/> + <see cref="IProjection"/>) materializing object state as
	/// documents in the host's <see cref="IMongoDatabase"/> registered under <paramref name="serviceKey"/> (unkeyed when
	/// null); its <see cref="IEventLog"/>, when there is one, is the one under the same key. Synqra never opens a client:
	/// the host owns the database. Key the store when a process hosts several Synqra features.
	/// </summary>
	public static IServiceCollection AddMongoProjection(this IServiceCollection services, string? serviceKey = null)
	{
		// The host is responsible for calling SynqraStreamContext.Enter(streamId) per request before touching the store.
		if (serviceKey is null)
		{
			services.TryAddSingleton<MongoProjection>();
			services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<MongoProjection>());
			services.AddSingleton<IProjection>(sp => sp.GetRequiredService<MongoProjection>());
		}
		else
		{
			services.TryAddKeyedSingleton<MongoProjection>(serviceKey);
			services.AddKeyedSingleton<IObjectStore>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<MongoProjection>(key));
			services.AddKeyedSingleton<IProjection>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<MongoProjection>(key));
		}
		// The projection is a derivation source for the event-log upgrades, and gets its own upgrades when its event log is not Mongo (one runner per host either way).
		services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SynqraMongoUpgradeService>());
		services.Configure<SynqraMongoUpgradeOptions>(o => o.Projections.Add(serviceKey));
		return services;
	}
}
