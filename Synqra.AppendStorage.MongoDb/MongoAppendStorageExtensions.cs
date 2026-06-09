using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

public class MongoAppendStorageOptions
{
	public static string DefaultCollectionName = "[TypeName]";

	public string ConnectionString { get; set; } = "mongodb://localhost";

	/// <summary>Collection name template; <c>[TypeName]</c> is replaced with the item type's name.</summary>
	public string CollectionName { get; set; } = DefaultCollectionName;
}

public static class MongoAppendStorageExtensions
{
	const string ConfigSectionKey = "Storage:MongoDbAppendStorage";
	static readonly object _configuredKey = new();

	public static IServiceCollection AddAppendStorageMongoDb<T>(
		  this IServiceCollection services
		, string connectionString
		, string? collectionName = null
		)
		where T : class
		=> AddAppendStorageMongoDb<T, Guid>(services, connectionString, collectionName);


	/// <summary>
	/// Same registration from an explicit connection string instead of a config section — for callers
	/// that already hold a connection string (e.g. a feature pointing at its own
	/// <c>ConnectionStrings</c> entry). It just populates <see cref="MongoAppendStorageOptions"/> from
	/// the given values and funnels through the same core/factory. <paramref name="databaseName"/>
	/// defaults to the database embedded in the connection string (then the options default).
	/// </summary>
	public static IServiceCollection AddAppendStorageMongoDb<T, TKey>(
		  this IServiceCollection services
		, string connectionString
		, string? collectionName = null
		)
		where T : class
		where TKey : notnull
	{
		services.Configure<MongoAppendStorageOptions>(o =>
		{
			o.ConnectionString = connectionString;
			o.CollectionName = collectionName ?? MongoAppendStorageOptions.DefaultCollectionName;
		});
		return services.AddMongoAppendStorageCore<T, TKey>();
	}

	/// <summary>
	/// The one place the storage is registered: class-maps the <see cref="Event"/> hierarchy (when
	/// applicable) and registers the single <see cref="IAppendStorage{T, TKey}"/> factory, which reads
	/// everything it needs from <see cref="MongoAppendStorageOptions"/>.
	/// </summary>
	private static IServiceCollection AddMongoAppendStorageCore<T, TKey>(this IServiceCollection services)
		where T : class
		where TKey : notnull
	{
		// Event hierarchy must be class-mapped before the driver serializes anything.
		if (typeof(Event).IsAssignableFrom(typeof(T)))
		{
			MongoEventClassMaps.Register();
		}

		services.TryAddKeyedSingleton<IMongoCollection<T>>("Synqra.AppendStorage", (sp, key) =>
		{
			var options = sp.GetRequiredService<IOptions<MongoAppendStorageOptions>>().Value;
			var url = new MongoUrl(options.ConnectionString);
			var client = new MongoClient(options.ConnectionString);
			var db = client.GetDatabase(string.IsNullOrWhiteSpace(url.DatabaseName) ? "synqra" : url.DatabaseName);
			var collectionName = options.CollectionName
				.Replace("[TypeName]", typeof(T).Name)
				.Replace("[Type]", typeof(T).Name)
				;
			return db.GetCollection<T>(collectionName);
		});

		services.TryAddSingleton<IAppendStorage<T, TKey>, MongoAppendStorage<T, TKey>>();
		return services;
	}

	/// <summary>
	/// Universal entry point: register a MongoDB-backed append storage for <typeparamref name="T"/>
	/// keyed by <typeparamref name="TKey"/> on an <see cref="IServiceCollection"/>, configured from
	/// the <c>Storage:MongoDbAppendStorage</c> section of <paramref name="configuration"/>.
	/// <para>
	/// This takes <see cref="IServiceCollection"/> + <see cref="IConfiguration"/> — the common
	/// denominator across every host. The server's <see cref="IHostApplicationBuilder"/> overloads
	/// forward here; a Blazor WebAssembly <c>Program.cs</c> (whose <c>WebAssemblyHostBuilder</c> does
	/// NOT implement <see cref="IHostApplicationBuilder"/>) and modular-DI feature contexts can call
	/// it directly via <c>builder.Services</c> + <c>builder.Configuration</c>.
	/// </para>
	/// </summary>
	public static IServiceCollection AddAppendStorageMongoDb<T, TKey>(
		  this IServiceCollection services
		, IConfiguration configuration
		, IDictionary<object, object>? properties = null
		)
		where T : class
		where TKey : notnull
	{
		EnsureOptionsBound(services, configuration, properties);
		return services.AddMongoAppendStorageCore<T, TKey>();
	}

	public static IServiceCollection AddAppendStorageMongoDb<T>(
		  this IServiceCollection services
		, IConfiguration configuration
		, IDictionary<object, object>? properties = null
		)
		where T : class
	{
		return AddAppendStorageMongoDb<T, Guid>(services, configuration, properties);
	}

	/// <summary>
	/// Server-host convenience: forwards to the <see cref="IServiceCollection"/> + <see cref="IConfiguration"/>
	/// core. Events are stored as native, queryable BSON documents; the <see cref="MongoEventClassMaps"/>
	/// are registered for the <see cref="Event"/> hierarchy so polymorphic events round-trip with the
	/// <c>_t</c> discriminator. No key-extraction delegate is needed — the key field maps to <c>_id</c>.
	/// </summary>
	public static IHostApplicationBuilder AddAppendStorageMongoDb<T, TKey>(
		  this IHostApplicationBuilder hostBuilder
		)
		where T : class
		where TKey : notnull
	{
		AddAppendStorageMongoDb<T, TKey>(hostBuilder.Services, hostBuilder.Configuration, hostBuilder.Properties);
		return hostBuilder;
	}

	public static IHostApplicationBuilder AddAppendStorageMongoDb<T>(
		  this IHostApplicationBuilder hostBuilder
		)
		where T : class
		=> hostBuilder.AddAppendStorageMongoDb<T, Guid>();

	/// <summary>Bind the options section exactly once, no matter how many item types register storage.</summary>
	private static void EnsureOptionsBound(IServiceCollection services, IConfiguration configuration, IDictionary<object, object>? properties)
	{
		if (properties == null || properties.TryAdd(_configuredKey, string.Empty))
		{
			services.Configure<MongoAppendStorageOptions>(configuration.GetSection(ConfigSectionKey));
		}
	}
}
