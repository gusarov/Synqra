using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// An append storage that can join a caller's Mongo session, so the events land in the same
/// transaction as the documents they describe: both commit, or neither does.
/// </summary>
public interface IMongoSessionAppendStorage<T>
{
	Task AppendBatchAsync(IClientSessionHandle session, IEnumerable<T> items, CancellationToken cancellationToken = default);
}
