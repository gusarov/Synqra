using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.MongoDb;

public class MongoDbBlobStorageOptions
{
	public string ConnectionString { get; set; } = "mongodb://localhost";
	public string DatabaseName { get; set; } = "synqra";
	public string CollectionName { get; set; } = "blobs";
}

public static class MongoDbBlobStorageExtensions
{
	private static readonly object SynqraMongoBlobStorageConfiguredKey = new();

	// IAppendStorage adapter on top of this blob storage is registered by Synqra.AppendStorage.BlobStorage.MongoDb (AddAppendStorageBlobMongoDb)

	public static IHostApplicationBuilder AddBlobStorageMongoDb<TKey>(this IHostApplicationBuilder hostBuilder, string storeName)
		where TKey : notnull, IComparable<TKey>
	{
		hostBuilder.AddBlobStorageMongoDbCore();
		hostBuilder.Services.TryAddKeyedSingleton<MongoDbBlobStorage<TKey>>(storeName, (serviceProvider, key) =>
			new MongoDbBlobStorage<TKey>(
				serviceProvider.GetRequiredService<IOptions<MongoDbBlobStorageOptions>>().Value,
				(string)key!));
		hostBuilder.Services.TryAddKeyedSingleton<IBlobStorage<TKey>>(storeName, (serviceProvider, key) =>
			serviceProvider.GetRequiredKeyedService<MongoDbBlobStorage<TKey>>((string)key!));
		return hostBuilder;
	}

	internal static void AddBlobStorageMongoDbCore(this IHostApplicationBuilder hostBuilder)
	{
		if (hostBuilder.Properties.TryAdd(SynqraMongoBlobStorageConfiguredKey, string.Empty))
		{
			hostBuilder.Services.Configure<MongoDbBlobStorageOptions>(hostBuilder.Configuration.GetSection("Storage:BlobStorage:MongoDb"));
		}
	}
}
