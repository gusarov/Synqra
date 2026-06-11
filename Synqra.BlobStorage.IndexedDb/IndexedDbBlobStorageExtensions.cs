using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
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
	// IAppendStorage adapter on top of this blob storage is registered by Synqra.AppendStorage.BlobStorage.IndexedDb (AddAppendStorageBlobIndexedDb)

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
