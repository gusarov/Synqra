using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using Synqra.BlobStorage;

namespace Synqra.AppendStorage.BlobStorage;

public static class BlobAppendStorageExtensions
{
	public static IHostApplicationBuilder AddAppendStorageBlob<T, TKey>(this IHostApplicationBuilder hostBuilder, string storeName, Func<T, TKey> getKey)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		hostBuilder.Services.AddAppendStorageBlob(storeName, getKey);
		return hostBuilder;
	}

	public static IServiceCollection AddAppendStorageBlob<T, TKey>(this IServiceCollection services, string storeName, Func<T, TKey> getKey)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		services.TryAddSingleton<IAppendStorage<T, TKey>>(serviceProvider =>
			new BlobAppendStorage<T, TKey>(
				serviceProvider.GetRequiredKeyedService<IBlobStorage<TKey>>(storeName),
				serviceProvider.GetRequiredService<ISbxSerializerFactory>(),
				getKey));
		return services;
	}
}
