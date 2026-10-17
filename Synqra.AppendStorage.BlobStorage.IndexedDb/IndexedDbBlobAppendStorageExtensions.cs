using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synqra.BlobStorage;
using Synqra.BlobStorage.IndexedDb;

namespace Synqra.AppendStorage.BlobStorage.IndexedDb;

/// <summary>
/// Layering glue: registers IndexedDb-backed <see cref="Synqra.BlobStorage.IBlobStorage{TKey}"/> together with the
/// <see cref="IAppendStorage{T, TKey}"/> adapter on top of it. Lives in a separate project so that
/// Synqra.BlobStorage.IndexedDb stays usable without any AppendStorage dependency.
/// </summary>
public static class IndexedDbBlobAppendStorageExtensions
{
	public static IServiceCollection AddAppendStorageBlobIndexedDb<T>(this IServiceCollection services, Func<T, Guid> keyAccessor, IConfiguration configuration, string? storeName = null)
		where T : class
	{
		return services.AddAppendStorageBlobIndexedDb(keyAccessor, x => x.ToString("N"), Guid.Parse, configuration, storeName);
	}

	/// <summary>A two-part key (an event log's (stream, sequence)): both parts as hex, so the text sorts like the key.</summary>
	public static IServiceCollection AddAppendStorageBlobIndexedDb<T>(this IServiceCollection services, Func<T, (Guid, Guid)> keyAccessor, IConfiguration configuration, string? storeName = null)
		where T : class
	{
		return services.AddAppendStorageBlobIndexedDb(keyAccessor, x => x.Item1.ToString("N") + x.Item2.ToString("N"), x => (Guid.Parse(x[..32]), Guid.Parse(x[32..])), configuration, storeName);
	}

	/// <summary>
	/// A browser event log on IndexedDB: <see cref="BlobEventStorage"/> keyed by (stream, sequence), which every tab of the
	/// browser may share. Each append stamps the next sequence after the store's tail and lands once per EventId, in one
	/// IndexedDB transaction, so tabs agree on one arrival order and one copy of each event. Compose the log on top with
	/// <c>AddClientEventLog()</c>.
	/// </summary>
	public static IServiceCollection AddEventStorageBlobIndexedDb(this IServiceCollection services, IConfiguration configuration, string storeName = "Events")
	{
		// IndexedDB exists only in a browser; elsewhere this registers nothing, like AddBlobStorageIndexedDb itself.
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
		{
			services.AddBlobStorageIndexedDb<(Guid Stream, Guid Sequence)>(storeName, x => x.Stream.ToString("N") + x.Sequence.ToString("N"), x => (Guid.Parse(x[..32]), Guid.Parse(x[32..])), configuration);
			services.TryAddSingleton<IBlobStorage<(Guid Stream, Guid Sequence)>>(sp => sp.GetRequiredKeyedService<IBlobStorage<(Guid Stream, Guid Sequence)>>(storeName));
			services.TryAddSingleton<IAppendStorage<Event, (Guid Stream, Guid Sequence)>, BlobEventStorage>();
		}
		return services;
	}

	public static IServiceCollection AddAppendStorageBlobIndexedDb<T, TKey>(
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
		services.AddAppendStorageBlob(storeName, keyAccessor);
		return services;
	}
}
