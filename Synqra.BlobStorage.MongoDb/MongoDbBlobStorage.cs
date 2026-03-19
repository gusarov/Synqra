using System.Runtime.CompilerServices;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.MongoDb;

public class MongoDbBlobStorage<TKey> : IBlobStorage<TKey>
	where TKey : notnull, IComparable<TKey>
{
	public MongoDbBlobStorage(MongoDbBlobStorageOptions options, string storeName)
	{
		throw new NotImplementedException("MongoDb blob storage is a placeholder and has not been implemented yet.");
	}

	public IAsyncEnumerable<TKey> EnumerateKeysAsync(TKey? from = default, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException("MongoDb blob storage is a placeholder and has not been implemented yet.");
	}

	public ValueTask<byte[]> ReadBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException("MongoDb blob storage is a placeholder and has not been implemented yet.");
	}

	public ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException("MongoDb blob storage is a placeholder and has not been implemented yet.");
	}

	public ValueTask DeleteBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException("MongoDb blob storage is a placeholder and has not been implemented yet.");
	}

	public void Dispose()
	{
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}
