using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using Synqra.BlobStorage;
using System.Text.Json;

namespace Synqra.AppendStorage.BlobStorage;

public static class BlobAppendStorageExtensions
{
	public static IHostApplicationBuilder AddAppendStorageBlob<T, TKey>(this IHostApplicationBuilder hostBuilder, string storeName, Func<T, TKey> getKey, string? jsonShadowStoreName = null)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		hostBuilder.Services.AddAppendStorageBlob(storeName, getKey, jsonShadowStoreName);
		return hostBuilder;
	}

	/// <summary>
	/// <paramref name="jsonShadowStoreName"/> is optional — when given, every append also
	/// writes a JSON copy to whatever <see cref="IBlobStorage{TKey}"/> is keyed-registered
	/// under that name (e.g. a second call to AddBlobStorageIndexedDb with a different
	/// store name), in addition to the real (SBX) write. Reads are unaffected; SBX stays
	/// the only thing GetAsync/GetAllAsync return. See BlobAppendStorage's own remarks.
	/// </summary>
	public static IServiceCollection AddAppendStorageBlob<T, TKey>(this IServiceCollection services, string storeName, Func<T, TKey> getKey, string? jsonShadowStoreName = null)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		services.TryAddSingleton<IAppendStorage<T, TKey>>(serviceProvider =>
			new BlobAppendStorage<T, TKey>(
				serviceProvider.GetRequiredKeyedService<IBlobStorage<TKey>>(storeName),
				serviceProvider.GetRequiredService<ISbxSerializerFactory>(),
				getKey,
				jsonShadowStoreName is null ? null : serviceProvider.GetRequiredKeyedService<IBlobStorage<TKey>>(jsonShadowStoreName),
				serviceProvider.GetService<JsonSerializerOptions>()));
		return services;
	}
}
