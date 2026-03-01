using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Synqra.AppendStorage.Sqlite;

public class SqliteAppendStorageOptions
{
	public string ConnectionString { get; set; } = "Data Source=synqra.db";
}

public static class SqliteAppendStorageExtensions
{
	static object _synqraSqliteStorageConfiguredKey = new object();

	public static void AddAppendStorageSqlite<T, TKey>(this IHostApplicationBuilder hostBuilder, Func<T, Guid> getKey)
		where T : class
	{
		hostBuilder.AddAppendStorageSqliteCore();
		hostBuilder.Services.AddSingleton(getKey);
		hostBuilder.Services.TryAddSingleton<IAppendStorage<T, TKey>, SqliteAppendStorage<T, TKey>>();
	}

	internal static void AddAppendStorageSqliteCore(this IHostApplicationBuilder hostBuilder)
	{
		if (hostBuilder.Properties.TryAdd(_synqraSqliteStorageConfiguredKey, string.Empty))
		{
			hostBuilder.Services.Configure<SqliteAppendStorageOptions>(hostBuilder.Configuration.GetSection("Storage:SqliteStorage"));
		}
	}
}
