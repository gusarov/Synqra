using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

public static class MongoEventStorageExtensions
{
	const string DefaultCollectionName = "Event";

	/// <summary>
	/// <see cref="MongoEventStorage"/> as the <c>IAppendStorage&lt;Event, (Guid, Guid)&gt;</c> under <paramref name="serviceKey"/>,
	/// over a collection of the host's <see cref="IMongoDatabase"/> registered under <paramref name="databaseKey"/> (default:
	/// the service key; both null = unkeyed). Synqra never opens a client: the host owns the database. Compose the log on
	/// top with <c>AddServerEventLog(serviceKey)</c>. Opening the storage opts it into the event-log upgrades.
	/// </summary>
	public static IServiceCollection AddMongoEventStorage(this IServiceCollection services, string? serviceKey = null, string? databaseKey = null, string? collectionName = null)
	{
		MongoEventClassMaps.Register(); // before the driver serializes anything
		var database = databaseKey ?? serviceKey;
		var name = collectionName ?? DefaultCollectionName;
		// MongoEventStorage takes the collection with [FromKeyedServices]: the collection shares the storage's key.
		if (serviceKey is null)
		{
			services.TryAddSingleton(sp => sp.GetRequiredKeyedService<IMongoDatabase>(database).GetCollection<Event>(name));
			services.TryAddSingleton<IAppendStorage<Event, (Guid Stream, Guid Sequence)>, MongoEventStorage>();
		}
		else
		{
			services.TryAddKeyedSingleton(serviceKey, (sp, _) => sp.GetRequiredKeyedService<IMongoDatabase>(database).GetCollection<Event>(name));
			services.TryAddKeyedSingleton<IAppendStorage<Event, (Guid Stream, Guid Sequence)>, MongoEventStorage>(serviceKey);
		}
		services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SynqraMongoUpgradeService>());
		services.Configure<SynqraMongoUpgradeOptions>(o => o.EventStorages.Add(serviceKey));
		return services;
	}
}
