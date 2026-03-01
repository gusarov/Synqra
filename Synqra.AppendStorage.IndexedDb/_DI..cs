using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Runtime.InteropServices;
// using TG.Blazor.IndexedDB;

namespace Synqra.AppendStorage.IndexedDb;

public static class IndexedDbAppendStorageExtensions
{
	public static void AddIndexedDbAppendStorage<T, TKey>(this IServiceCollection services)
		where T : class
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
		{
			services.TryAddSingleton<IndexedDbJsInterop>();
			services.TryAddSingleton<IAppendStorage<T, TKey>, IndexedDbAppendStorage<T, TKey>>();
			/*
			services.AddIndexedDB(dbStore => {
				dbStore.DbName = "TgNugetStore";
				dbStore.Version = 1;

				dbStore.Stores.Add(new StoreSchema
				{
					Name = "Events",
					PrimaryKey = new IndexSpec { Name = "seq_id", KeyPath = "seq_id", Auto = true },
				});
			});
			*/
		}
	}
}
