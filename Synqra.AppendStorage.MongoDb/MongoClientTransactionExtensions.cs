using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>One session and one transaction around a piece of work; the driver retries it on a transient conflict.</summary>
public static class MongoClientTransactionExtensions
{
	public static async Task<T> RunInTransactionAsync<T>(this IMongoClient client, Func<IClientSessionHandle, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
	{
		using var session = await client.StartSessionAsync(cancellationToken: cancellationToken);
		return await session.WithTransactionAsync((s, ct) => work(s, ct), cancellationToken: cancellationToken);
	}

	public static Task RunInTransactionAsync(this IMongoClient client, Func<IClientSessionHandle, CancellationToken, Task> work, CancellationToken cancellationToken = default)
		=> client.RunInTransactionAsync(async (s, ct) =>
		{
			await work(s, ct);
			return 0; // the driver's callback must produce a value; this work has none
		}, cancellationToken);
}
