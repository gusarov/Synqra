using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

public class MongoAppendStorageOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "synqra";
    /// <summary>Collection name template; <c>[TypeName]</c> is replaced with the item type's name.</summary>
    public string CollectionName { get; set; } = "[TypeName]";
}

public static class MongoAppendStorageExtensions
{
    static readonly object _configuredKey = new();

    /// <summary>
    /// Register a MongoDB-backed append storage for <typeparamref name="T"/> keyed by
    /// <typeparamref name="TKey"/>. Events are stored as native BSON documents; the
    /// <see cref="MongoEventClassMaps"/> are registered for the <see cref="Event"/>
    /// hierarchy so polymorphic events round-trip with the <c>_t</c> discriminator.
    /// <para>
    /// Unlike the file/line backends, no key-extraction delegate is needed: the key
    /// field is mapped to <c>_id</c> in the BSON class map, so Mongo reads the key from
    /// the document itself on insert and the storage queries/orders by <c>_id</c>.
    /// </para>
    /// </summary>
    public static IHostApplicationBuilder AddAppendStorageMongoDb<T, TKey>(
        this IHostApplicationBuilder hostBuilder,
        string? collectionName = null)
        where T : class
        where TKey : notnull
    {
        hostBuilder.AddAppendStorageMongoDbCore();

        // Event hierarchy must be class-mapped before the driver serializes anything.
        if (typeof(Event).IsAssignableFrom(typeof(T)))
        {
            MongoEventClassMaps.Register();
        }

        hostBuilder.Services.TryAddSingleton<IAppendStorage<T, TKey>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MongoAppendStorageOptions>>().Value;
            var client = sp.GetService<IMongoClient>() ?? new MongoClient(options.ConnectionString);
            var db = client.GetDatabase(options.DatabaseName);
            var name = (collectionName ?? options.CollectionName)
                .Replace("[TypeName]", typeof(T).Name)
                .Replace("[Type]", typeof(T).Name);
            var collection = db.GetCollection<T>(name);
            return new MongoAppendStorage<T, TKey>(collection);
        });
        return hostBuilder;
    }

    public static IHostApplicationBuilder AddAppendStorageMongoDb<T>(
        this IHostApplicationBuilder hostBuilder,
        string? collectionName = null)
        where T : class
        => hostBuilder.AddAppendStorageMongoDb<T, Guid>(collectionName);

    internal static void AddAppendStorageMongoDbCore(this IHostApplicationBuilder hostBuilder)
    {
        if (hostBuilder.Properties.TryAdd(_configuredKey, string.Empty))
        {
            hostBuilder.Services.Configure<MongoAppendStorageOptions>(
                hostBuilder.Configuration.GetSection("Storage:MongoDbAppendStorage"));
        }
    }
}
