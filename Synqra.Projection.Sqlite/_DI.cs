using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;

namespace Synqra.Projection.Sqlite;

public static class SqliteSynqraExtensions
{
	static SqliteSynqraExtensions()
	{
		// AOT ROOTS:
		_ = typeof(IAppendStorage<Event, Guid>);
	}

	public static IHostApplicationBuilder AddSqliteSynqraStore(this IHostApplicationBuilder builder)
	{
		builder.Services.AddSingleton<SqliteDatabaseContext>();
		builder.Services.AddSingleton<SqliteStore>();
		builder.Services.AddSingleton<SqliteProjection>();
		builder.Services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<SqliteStore>());
		builder.Services.AddSingleton<IProjection>(sp => sp.GetRequiredService<SqliteProjection>());
		// builder.Services.AddSingleton(typeof(IStoreCollection<>), (sp, s) => sp.GetRequiredService<IStoreContext>().Get<>); // Example storage implementation
		return builder;
	}
}
