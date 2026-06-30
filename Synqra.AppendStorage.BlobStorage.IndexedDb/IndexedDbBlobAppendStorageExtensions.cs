using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Synqra.BlobStorage.IndexedDb;

namespace Synqra.AppendStorage.BlobStorage.IndexedDb;

/// <summary>
/// Layering glue: registers IndexedDb-backed <see cref="Synqra.BlobStorage.IBlobStorage{TKey}"/> together with the
/// <see cref="IAppendStorage{T, TKey}"/> adapter on top of it. Lives in a separate project so that
/// Synqra.BlobStorage.IndexedDb stays usable without any AppendStorage dependency.
/// </summary>
public static class IndexedDbBlobAppendStorageExtensions
{
	public static IServiceCollection AddAppendStorageBlobIndexedDb<T>(this IServiceCollection services, Func<T, Guid> keyAccessor, IConfiguration configuration, string? storeName = null, bool withJsonShadow = false)
		where T : class
	{
		return services.AddAppendStorageBlobIndexedDb(keyAccessor, x => x.ToString("N"), Guid.Parse, configuration, storeName, withJsonShadow);
	}

	/// <summary>
	/// <paramref name="withJsonShadow"/>: when true, every append also writes a second,
	/// human-inspectable JSON copy to its own IndexedDb object store ("{storeName}.json"),
	/// in addition to the real (SBX) write — reads are unaffected, SBX stays authoritative.
	/// For stores where SBX read-back isn't safe to rely on yet (no schema-evolution
	/// handling in place for IndexedDb specifically). See BlobAppendStorage's own remarks.
	/// </summary>
	public static IServiceCollection AddAppendStorageBlobIndexedDb<T, TKey>(
		this IServiceCollection services,
		Func<T, TKey> keyAccessor,
		Func<TKey, string> getKeyText,
		Func<string, TKey> getKeyFromText,
		IConfiguration configuration,
		string? storeName = null,
		bool withJsonShadow = false)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		storeName ??= typeof(T).Name;
		services.AddBlobStorageIndexedDb(storeName, getKeyText, getKeyFromText, configuration);
		string? jsonShadowStoreName = null;
		if (withJsonShadow)
		{
			jsonShadowStoreName = storeName + ".json";
			services.AddBlobStorageIndexedDb(jsonShadowStoreName, getKeyText, getKeyFromText, configuration);
		}
		services.AddAppendStorageBlob(storeName, keyAccessor, jsonShadowStoreName);
		return services;
	}
}
