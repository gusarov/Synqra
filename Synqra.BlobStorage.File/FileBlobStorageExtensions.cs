using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage.BlobStorage;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.File;

public class FileBlobStorageOptions
{
	public string Folder { get; set; } = Path.Combine("storage", "[Store]") + Path.DirectorySeparatorChar;
}

public static class FileBlobStorageExtensions
{
	private static readonly object SynqraFileBlobStorageConfiguredKey = new();

	public static IHostApplicationBuilder AddBlobStorageFile<T>(this IHostApplicationBuilder hostBuilder, Func<T, (Guid, Guid)> getKey, string? storeName = null)
		where T : class
	{
		return hostBuilder.AddBlobStorageFile(
			getKey,
			x => x.Item1.ToString("N") + x.Item2.ToString("N"),
			path =>
			{
				var normalized = path.Replace(Path.DirectorySeparatorChar.ToString(), string.Empty).Replace(Path.AltDirectorySeparatorChar.ToString(), string.Empty);
				return (Guid.Parse(normalized[..32]), Guid.Parse(normalized[32..]));
			},
			storeName);
	}

	public static IHostApplicationBuilder AddBlobStorageFile<T>(this IHostApplicationBuilder hostBuilder, Func<T, Guid> getKey, string? storeName = null)
		where T : class
	{
		return hostBuilder.AddBlobStorageFile(getKey, x => x.ToString("N"), Guid.Parse, storeName);
	}

	public static IHostApplicationBuilder AddBlobStorageFile<T, TKey>(
		this IHostApplicationBuilder hostBuilder,
		Func<T, TKey> getKey,
		Func<TKey, string> getKeyHex,
		Func<string, TKey> getHexKey,
		string? storeName = null)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		storeName ??= typeof(T).Name;
		hostBuilder.AddBlobStorageFile(storeName, getKeyHex, getHexKey);
		hostBuilder.AddBlobAppendStorage(storeName, getKey);
		return hostBuilder;
	}

	public static IHostApplicationBuilder AddBlobStorageFile<TKey>(
		this IHostApplicationBuilder hostBuilder,
		string storeName,
		Func<TKey, string> getKeyHex,
		Func<string, TKey> getHexKey)
		where TKey : notnull, IComparable<TKey>
	{
		hostBuilder.AddBlobStorageFileCore();
		hostBuilder.Services.TryAddKeyedSingleton<FileBlobStorage<TKey>>(storeName, (serviceProvider, key) =>
			new FileBlobStorage<TKey>(
				serviceProvider.GetRequiredService<IOptions<FileBlobStorageOptions>>().Value,
				(string)key!,
				getKeyHex,
				getHexKey));
		hostBuilder.Services.TryAddKeyedSingleton<IBlobStorage<TKey>>(storeName, (serviceProvider, key) =>
			serviceProvider.GetRequiredKeyedService<FileBlobStorage<TKey>>((string)key!));
		return hostBuilder;
	}

	internal static void AddBlobStorageFileCore(this IHostApplicationBuilder hostBuilder)
	{
		if (hostBuilder.Properties.TryAdd(SynqraFileBlobStorageConfiguredKey, string.Empty))
		{
			hostBuilder.Services.Configure<FileBlobStorageOptions>(hostBuilder.Configuration.GetSection("Storage:BlobStorage:File"));
		}
	}
}
