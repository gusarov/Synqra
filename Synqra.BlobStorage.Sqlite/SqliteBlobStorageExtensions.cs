using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage.BlobStorage;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.Sqlite;

public class SqliteBlobStorageOptions
{
	public string ConnectionString { get; set; } = "Data Source=synqra.db";
}

public static class SqliteBlobStorageExtensions
{
	private static readonly object SynqraSqliteBlobStorageConfiguredKey = new();

	public static IHostApplicationBuilder AddBlobStorageSqlite<T, TKey>(this IHostApplicationBuilder hostBuilder, Func<T, TKey> getKey, string? storeName = null)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		storeName ??= typeof(T).Name;
		hostBuilder.AddBlobStorageSqlite<TKey>(storeName);
		hostBuilder.AddBlobAppendStorage(storeName, getKey);
		return hostBuilder;
	}

	public static IHostApplicationBuilder AddBlobStorageSqlite<TKey>(this IHostApplicationBuilder hostBuilder, string storeName)
		where TKey : notnull, IComparable<TKey>
	{
		hostBuilder.AddBlobStorageSqliteCore();
		hostBuilder.Services.TryAddKeyedSingleton<SqliteBlobStorage<TKey>>(storeName, (serviceProvider, key) =>
			new SqliteBlobStorage<TKey>(
				serviceProvider.GetRequiredService<IOptions<SqliteBlobStorageOptions>>().Value,
				(string)key!));
		hostBuilder.Services.TryAddKeyedSingleton<IBlobStorage<TKey>>(storeName, (serviceProvider, key) =>
			serviceProvider.GetRequiredKeyedService<SqliteBlobStorage<TKey>>((string)key!));
		return hostBuilder;
	}

	internal static void AddBlobStorageSqliteCore(this IHostApplicationBuilder hostBuilder)
	{
		if (hostBuilder.Properties.TryAdd(SynqraSqliteBlobStorageConfiguredKey, string.Empty))
		{
			hostBuilder.Services.Configure<SqliteBlobStorageOptions>(hostBuilder.Configuration.GetSection("Storage:BlobStorage:Sqlite"));
		}
	}
}
