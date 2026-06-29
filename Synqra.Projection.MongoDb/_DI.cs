using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Synqra.AppendStorage;

namespace Synqra.Projection.MongoDb;

public class MongoProjectionOptions
{
	public string ConnectionString { get; set; } = "mongodb://localhost";

	/// <summary>Database for the materialized read-model. Falls back to the connection-string database, then <c>synqra</c>.</summary>
	public string? DatabaseName { get; set; }
}

public static class MongoSynqraStoreExtensions
{
	const string ConfigSectionKey = "Storage:MongoDbProjection";

	static MongoSynqraStoreExtensions()
	{
		// AOT ROOTS:
		_ = typeof(IAppendStorage<Event, Guid>);
	}

	/// <summary>
	/// Register a MongoDB-backed Synqra store (<see cref="IObjectStore"/> + <see cref="IProjection"/>),
	/// materializing object state as documents, configured from the <c>Storage:MongoDbProjection</c>
	/// section. Takes <see cref="IServiceCollection"/> + <see cref="IConfiguration"/> so it works for the
	/// server host, a Blazor WASM host, and modular-DI feature contexts alike.
	/// </summary>
	public static IServiceCollection AddMongoDbSynqraStore(this IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<MongoProjectionOptions>(configuration.GetSection(ConfigSectionKey));
		return services.AddMongoDbSynqraStoreCore();
	}

	/// <summary>Register from an explicit connection string (caller already holds one).</summary>
	public static IServiceCollection AddMongoDbSynqraStore(this IServiceCollection services, string connectionString, string? databaseName = null)
	{
		services.Configure<MongoProjectionOptions>(o =>
		{
			o.ConnectionString = connectionString;
			if (!string.IsNullOrWhiteSpace(databaseName))
			{
				o.DatabaseName = databaseName;
			}
		});
		return services.AddMongoDbSynqraStoreCore();
	}

	public static IHostApplicationBuilder AddMongoDbSynqraStore(this IHostApplicationBuilder hostBuilder)
	{
		hostBuilder.Services.AddMongoDbSynqraStore(hostBuilder.Configuration);
		return hostBuilder;
	}

	/// <summary>
	/// Keyed variant — for a process hosting more than one independent Synqra-backed feature
	/// (each with its own Mongo connection/database). The plain (unkeyed) overloads register
	/// IObjectStore/IProjection/MongoProjection itself as the single, global, unkeyed singleton
	/// slot — fine with exactly one consumer in the process, but every later caller silently
	/// replaces the earlier one's registration ("last wins"), and every consumer ends up sharing
	/// whichever store happened to register last, regardless of which one it actually asked for.
	/// Confirmed in a real host: two independent features each calling the unkeyed overload, the
	/// second silently winning, the first's own background service then crashing trying to cast
	/// the WRONG feature's documents to its own model types. Give each feature its own
	/// <paramref name="serviceKey"/> and this never happens — each gets a fully independent
	/// MongoProjection instance under its own key.
	/// </summary>
	public static IServiceCollection AddMongoDbSynqraStore(this IServiceCollection services, string serviceKey, string connectionString, string? databaseName = null)
	{
		services.Configure<MongoProjectionOptions>(serviceKey, o =>
		{
			o.ConnectionString = connectionString;
			if (!string.IsNullOrWhiteSpace(databaseName))
			{
				o.DatabaseName = databaseName;
			}
		});
		return services.AddMongoDbSynqraStoreCore(serviceKey);
	}

	static IServiceCollection AddMongoDbSynqraStoreCore(this IServiceCollection services)
	{
		services.AddSingleton<MongoProjection>(sp =>
		{
			var options = sp.GetRequiredService<IOptions<MongoProjectionOptions>>().Value;
			var url = new MongoUrl(options.ConnectionString);
			var databaseName = !string.IsNullOrWhiteSpace(options.DatabaseName)
				? options.DatabaseName!
				: string.IsNullOrWhiteSpace(url.DatabaseName) ? "synqra" : url.DatabaseName;
			var database = new MongoClient(options.ConnectionString).GetDatabase(databaseName);
			// database is the only non-DI argument; the rest of MongoProjection's dependencies resolve from DI.
			return ActivatorUtilities.CreateInstance<MongoProjection>(sp, database);
		});
		services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<MongoProjection>());
		services.AddSingleton<IProjection>(sp => sp.GetRequiredService<MongoProjection>());
		return services;
	}

	static IServiceCollection AddMongoDbSynqraStoreCore(this IServiceCollection services, string serviceKey)
	{
		services.AddKeyedSingleton<MongoProjection>(serviceKey, (sp, key) =>
		{
			var options = sp.GetRequiredService<IOptionsMonitor<MongoProjectionOptions>>().Get((string)key!);
			var url = new MongoUrl(options.ConnectionString);
			var databaseName = !string.IsNullOrWhiteSpace(options.DatabaseName)
				? options.DatabaseName!
				: string.IsNullOrWhiteSpace(url.DatabaseName) ? "synqra" : url.DatabaseName;
			var database = new MongoClient(options.ConnectionString).GetDatabase(databaseName);
			return ActivatorUtilities.CreateInstance<MongoProjection>(sp, database);
		});
		services.AddKeyedSingleton<IObjectStore>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<MongoProjection>(key));
		services.AddKeyedSingleton<IProjection>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<MongoProjection>(key));
		return services;
	}
}
