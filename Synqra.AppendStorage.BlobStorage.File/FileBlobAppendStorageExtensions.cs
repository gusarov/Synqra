using Microsoft.Extensions.Hosting;
using Synqra.BlobStorage.File;

namespace Synqra.AppendStorage.BlobStorage.File;

/// <summary>
/// Layering glue: registers file-backed <see cref="Synqra.BlobStorage.IBlobStorage{TKey}"/> together with the
/// <see cref="IAppendStorage{T, TKey}"/> adapter on top of it. Lives in a separate project so that
/// Synqra.BlobStorage.File stays usable without any AppendStorage dependency.
/// </summary>
public static class FileBlobAppendStorageExtensions
{
	public static IHostApplicationBuilder AddAppendStorageBlobFile<T>(this IHostApplicationBuilder hostBuilder, Func<T, (Guid, Guid)> getKey, string? storeName = null)
		where T : class
	{
		storeName ??= typeof(T).Name;
		hostBuilder.AddBlobStorageFile(getKey, storeName);
		hostBuilder.AddAppendStorageBlob(storeName, getKey);
		return hostBuilder;
	}

	public static IHostApplicationBuilder AddAppendStorageBlobFile<T>(this IHostApplicationBuilder hostBuilder, Func<T, Guid> getKey, string? storeName = null)
		where T : class
	{
		storeName ??= typeof(T).Name;
		hostBuilder.AddBlobStorageFile(getKey, storeName);
		hostBuilder.AddAppendStorageBlob(storeName, getKey);
		return hostBuilder;
	}

	public static IHostApplicationBuilder AddAppendStorageBlobFile<T, TKey>(
		this IHostApplicationBuilder hostBuilder,
		Func<T, TKey> getKey,
		Func<TKey, string> getKeyHex,
		Func<string, TKey> getHexKey,
		string? storeName = null)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		storeName ??= typeof(T).Name;
		hostBuilder.AddBlobStorageFile(getKey, getKeyHex, getHexKey, storeName);
		hostBuilder.AddAppendStorageBlob(storeName, getKey);
		return hostBuilder;
	}
}
