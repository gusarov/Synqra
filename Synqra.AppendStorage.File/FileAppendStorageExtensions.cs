using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Synqra.AppendStorage.File;

public class FileAppendStorageOptions
{
	public string Folder { get; set; } = "storage" + Path.DirectorySeparatorChar;
}

public static class FileAppendStorageExtensions
{
	static object _synqraSqliteStorageConfiguredKey = new object();

	public static void AddAppendStorageFile<T, TKey>(this IHostApplicationBuilder hostBuilder, Func<T, TKey> getKey)
	{
		hostBuilder.AddAppendStorageFileCore();
		hostBuilder.Services.AddSingleton(getKey);
		hostBuilder.Services.TryAddSingleton<IAppendStorage<T, TKey>, FileAppendStorage<T, TKey>>();
	}

	internal static void AddAppendStorageFileCore(this IHostApplicationBuilder hostBuilder)
	{
		if (hostBuilder.Properties.TryAdd(_synqraSqliteStorageConfiguredKey, string.Empty))
		{
			hostBuilder.Services.Configure<FileAppendStorageOptions>(hostBuilder.Configuration.GetSection("Storage:FileStorage"));
		}
	}
}
