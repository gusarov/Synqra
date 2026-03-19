using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage.BlobStorage;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.MongoDb;

public class MongoDbBlobStorageOptions
{
	public string ConnectionString { get; set; } = "mongodb://localhost:27017";
	public string DatabaseName { get; set; } = "synqra";
	public string CollectionName { get; set; } = "blobs";
}

public static class MongoDbBlobStorageExtensions
{
	private static readonly object SynqraMongoBlobStorageConfiguredKey = new();

	public static IHostApplicationBuilder AddBlobStorageMongoDb<T, TKey>(this IHostApplicationBuilder hostBuilder, Func<T, TKey> getKey, string? storeName = null)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		storeName ??= typeof(T).Name;
		hostBuilder.AddBlobStorageMongoDb<TKey>(storeName);
		hostBuilder.AddBlobAppendStorage(storeName, getKey);
		return hostBuilder;
	}

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
