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

	public static void AddAppendStorageFile<T>(this IHostApplicationBuilder hostBuilder, Func<T, (Guid, Guid)> getKey)
		where T : class
	{
		AddAppendStorageFile(hostBuilder, getKey, x => x.Item1.ToString("N") + x.Item2.ToString("N"), path => (Guid.Parse(path.Replace(Path.DirectorySeparatorChar + "", "")[..32]), Guid.Parse(path.Replace(Path.DirectorySeparatorChar + "", "")[32..])));
	}

	public static void AddAppendStorageFile<T>(this IHostApplicationBuilder hostBuilder, Func<T, Guid> getKey)
		where T : class
	{
		AddAppendStorageFile(hostBuilder, getKey, x => x.ToString("N"), Guid.Parse);
	}

	public static void AddAppendStorageFile<T, TKey>(this IHostApplicationBuilder hostBuilder
		, Func<T, TKey> getKey
		, Func<TKey, string> getKeyHex
		, Func<string, TKey> getHexKey
		)
		where T : class
	{
		hostBuilder.AddAppendStorageFileCore();
		hostBuilder.Services.TryAddSingleton<IAppendStorage<T, TKey>, FileAppendStorage<T, TKey>>();
		hostBuilder.Services.AddSingleton(getKey);
		hostBuilder.Services.AddSingleton(getKeyHex);
		hostBuilder.Services.AddSingleton(getHexKey);
	}

	internal static void AddAppendStorageFileCore(this IHostApplicationBuilder hostBuilder)
	{
		if (hostBuilder.Properties.TryAdd(_synqraSqliteStorageConfiguredKey, string.Empty))
		{
			hostBuilder.Services.Configure<FileAppendStorageOptions>(hostBuilder.Configuration.GetSection("Storage:FileStorage"));
		}
	}
}
