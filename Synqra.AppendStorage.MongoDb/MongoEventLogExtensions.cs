using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

public static class MongoEventLogExtensions
{
	const string DefaultCollectionName = "Event";

	/// <summary>
	/// The server's event log on Mongo: a <see cref="MongoEventStorage"/> (registered as the <c>IAppendStorage&lt;Event, (Guid, Guid)&gt;</c>)
	/// under a <see cref="ServerEventLog"/> (registered as <see cref="IEventLog"/>, the one consumers ask for), keyed by
	/// <paramref name="serviceKey"/> when given — a process hosting more than one Synqra feature keys each. Opening the log
	/// also opts it into the event-log storage upgrades. The database is the connection string's, else <c>synqra</c>.
	/// </summary>
	public static IServiceCollection AddMongoEventLog(this IServiceCollection services, string connectionString, string? collectionName = null, string? serviceKey = null)
	{
		MongoEventClassMaps.Register(); // before the driver serializes anything
		var name = collectionName ?? DefaultCollectionName;
		// The store and its collection share the key: MongoEventStorage takes the collection with [FromKeyedServices].
		if (serviceKey is null)
		{
			services.TryAddSingleton(_ => services.OpenDatabase(connectionString).GetCollection<Event>(name));
			services.TryAddSingleton<IAppendStorage<Event, (Guid Stream, Guid Sequence)>, MongoEventStorage>();
		}
		else
		{
			services.TryAddKeyedSingleton(serviceKey, (_, _) => services.OpenDatabase(connectionString).GetCollection<Event>(name));
			services.TryAddKeyedSingleton<IAppendStorage<Event, (Guid Stream, Guid Sequence)>, MongoEventStorage>(serviceKey);
		}
		services.AddServerEventLog(serviceKey);
		services.AnnounceForUpgrades(serviceKey, connectionString, name);
		return services;
	}

	/// <summary>
	/// Announces the event-log database to <see cref="SynqraMongoUpgradeService"/> and registers the runner once per host
	/// (TryAddEnumerable): opening the log IS opting into its upgrades; a consumer never wires anything.
	/// </summary>
	static void AnnounceForUpgrades(this IServiceCollection services, string? serviceKey, string connectionString, string collectionName)
	{
		services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SynqraMongoUpgradeService>());
		services.AddSingleton(sp =>
		{
			var database = services.OpenDatabase(connectionString);
			return new SynqraMongoUpgradeParticipant(serviceKey, SynqraMongoDatabaseRole.Events, _ => database, collectionName);
		});
	}

	static IMongoDatabase OpenDatabase(this IServiceCollection services, string connectionString)
	{
		var url = new MongoUrl(connectionString);
		return new MongoClient(connectionString).GetDatabase(string.IsNullOrWhiteSpace(url.DatabaseName) ? "synqra" : url.DatabaseName);
	}
}
