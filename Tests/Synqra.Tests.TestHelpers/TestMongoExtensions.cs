using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Synqra.Tests.TestHelpers;

public static class TestMongoExtensions
{
	/// <summary>
	/// The test database as the host's unkeyed <see cref="IMongoDatabase"/>, the way a host registers its own: Synqra's
	/// Mongo registrations read it and never open a client. One client per host; a second call keeps the first.
	/// </summary>
	public static IServiceCollection AddTestMongoDatabase(this IServiceCollection services, string? connectionString)
	{
		var url = new MongoUrl(connectionString);
		services.TryAddSingleton<IMongoClient>(_ => new MongoClient(url));
		services.TryAddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(string.IsNullOrWhiteSpace(url.DatabaseName) ? "synqra" : url.DatabaseName));
		return services;
	}
}
