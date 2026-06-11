using Microsoft.Extensions.Hosting;
using Synqra.BlobStorage.MongoDb;

namespace Synqra.AppendStorage.BlobStorage.MongoDb;

/// <summary>
/// Layering glue: registers MongoDb-backed <see cref="Synqra.BlobStorage.IBlobStorage{TKey}"/> together with the
/// <see cref="IAppendStorage{T, TKey}"/> adapter on top of it. Lives in a separate project so that
/// Synqra.BlobStorage.MongoDb stays usable without any AppendStorage dependency.
/// </summary>
public static class MongoDbBlobAppendStorageExtensions
{
	public static IHostApplicationBuilder AddAppendStorageBlobMongoDb<T, TKey>(this IHostApplicationBuilder hostBuilder, Func<T, TKey> getKey, string? storeName = null)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		storeName ??= typeof(T).Name;
		hostBuilder.AddBlobStorageMongoDb<TKey>(storeName);
		hostBuilder.AddAppendStorageBlob(storeName, getKey);
		return hostBuilder;
	}
}
