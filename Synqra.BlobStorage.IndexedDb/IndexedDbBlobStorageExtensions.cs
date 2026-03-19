using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using Synqra.AppendStorage.BlobStorage;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.IndexedDb;

public class IndexedDbBlobStorageOptions
{
	public string DatabaseName { get; set; } = "Synqra";
	public string ObjectStoreName { get; set; } = "blobs";
	public bool PopulateDebugJson { get; set; }
#if DEBUG
		= true;
#endif
}

public static class IndexedDbBlobStorageExtensions
{
	public static IServiceCollection AddBlobStorageIndexedDb<T>(this IServiceCollection services, Func<T, Guid> keyAccessor, IConfiguration configuration, string? storeName = null)
		where T : class
	{
		return services.AddBlobStorageIndexedDb(keyAccessor, x => x.ToString("N"), Guid.Parse, configuration, storeName);
	}

	public static IServiceCollection AddBlobStorageIndexedDb<T, TKey>(
		this IServiceCollection services,
		Func<T, TKey> keyAccessor,
		Func<TKey, string> getKeyText,
		Func<string, TKey> getKeyFromText,
		IConfiguration configuration,
		string? storeName = null)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		storeName ??= typeof(T).Name;
		services.AddBlobStorageIndexedDb(storeName, getKeyText, getKeyFromText, configuration);
		services.AddBlobAppendStorage(storeName, keyAccessor);
		return services;
	}

	public static IServiceCollection AddBlobStorageIndexedDb<TKey>(
		this IServiceCollection services,
		string storeName,
		Func<TKey, string> getKeyText,
		Func<string, TKey> getKeyFromText,
		IConfiguration configuration)
		where TKey : notnull, IComparable<TKey>
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
		{
			return services;
		}

		services.AddOptions<IndexedDbBlobStorageOptions>()
			.Bind(configuration.GetSection("Storage:BlobStorage:IndexedDb"));
		services.TryAddSingleton<IndexedDbJsInterop>();
		services.TryAddKeyedSingleton<IndexedDbBlobStorage<TKey>>(storeName, (serviceProvider, key) =>
			new IndexedDbBlobStorage<TKey>(
				serviceProvider.GetRequiredService<IndexedDbJsInterop>(),
				(string)key!,
				getKeyText,
				getKeyFromText));
		services.TryAddKeyedSingleton<IBlobStorage<TKey>>(storeName, (serviceProvider, key) =>
			serviceProvider.GetRequiredKeyedService<IndexedDbBlobStorage<TKey>>((string)key!));
		return services;
	}
}
