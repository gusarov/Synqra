using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Synqra.BinarySerializer;
using Synqra.AppendStorage;
using Synqra.BlobStorage;

namespace Synqra.AppendStorage.BlobStorage;

public class BlobAppendStorage<T, TKey> : IAppendStorage<T, TKey>
	where T : class
	where TKey : notnull, IComparable<TKey>
{
	private const int StackBufferSize = 16 * 1024;
	private const int InitialHeapBufferSize = 64 * 1024;

	private readonly IBlobStorage<TKey> _blobStorage;
	private readonly ISbxSerializer _serializer;
	private readonly ISbxSerializer _deserializer;
	private readonly Func<T, TKey> _getKeyFromItem;
	private readonly ConcurrentDictionary<TKey, WeakReference> _attachedObjectsById = new();

	public BlobAppendStorage(
		  IBlobStorage<TKey> blobStorage
		, ISbxSerializerFactory serializerFactory
		, Func<T, TKey> getKeyFromItem
		)
	{
		_blobStorage = blobStorage;
		_serializer = serializerFactory.CreateSerializer();
		_deserializer = serializerFactory.CreateSerializer();
		_getKeyFromItem = getKeyFromItem;
	}

	public Task<string> TestAsync(string input) => Task.FromResult(input);

	public Task AppendAsync(T item, CancellationToken cancellationToken = default)
	{
		if (_blobStorage.SupportsSyncOperations)
		{
			WriteSync(item);
			Attach(item);
			return Task.CompletedTask;
		}
		return WriteAsyncCore(item, cancellationToken);
	}

	public async Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
	{
		foreach (var item in items)
		{
			await AppendAsync(item, cancellationToken);
		}
	}

	public async Task<T> GetAsync(TKey key, CancellationToken cancellationToken = default)
	{
		if (TryGetAttachedObject(key, out var attached))
		{
			return attached;
		}

		var blob = await _blobStorage.ReadBlobAsync(key, cancellationToken);
		var item = Deserialize(blob);
		Attach(key, item);
		return item;
	}

	public async IAsyncEnumerable<T> GetAllAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await foreach (var key in _blobStorage.EnumerateKeysAsync(from, cancellationToken))
		{
			if (TryGetAttachedObject(key, out var attached))
			{
				yield return attached;
				continue;
			}

			var blob = await _blobStorage.ReadBlobAsync(key, cancellationToken);
			var item = Deserialize(blob);
			Attach(key, item);
			yield return item;
		}
	}

	public Task FlushAsync(CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_blobStorage.Dispose();
	}

	public ValueTask DisposeAsync()
	{
		return _blobStorage.DisposeAsync();
	}

	private void WriteSync(T item)
	{
		var key = _getKeyFromItem(item);

		Span<byte> stackBuffer = stackalloc byte[StackBufferSize];
		if (TrySerialize(item, stackBuffer, out var bytesWritten))
		{
			_blobStorage.WriteBlob(key, stackBuffer[..bytesWritten]);
			return;
		}

		byte[] rented = ArrayPool<byte>.Shared.Rent(InitialHeapBufferSize);
		try
		{
			while (true)
			{
				if (TrySerialize(item, rented.AsSpan(), out bytesWritten))
				{
					_blobStorage.WriteBlob(key, rented.AsSpan(0, bytesWritten));
					return;
				}
				ArrayPool<byte>.Shared.Return(rented);
				rented = ArrayPool<byte>.Shared.Rent(rented.Length * 2);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	private async Task WriteAsyncCore(T item, CancellationToken cancellationToken)
	{
		var key = _getKeyFromItem(item);
		Span<byte> stackBuffer = stackalloc byte[StackBufferSize];
		if (TrySerialize(item, stackBuffer, out var bytesWritten))
		{
			byte[] data = stackBuffer[..bytesWritten].ToArray();
			await _blobStorage.WriteBlobAsync(key, data, cancellationToken);
			Attach(item);
			return;
		}

		byte[] rented = ArrayPool<byte>.Shared.Rent(InitialHeapBufferSize);
		try
		{
			while (true)
			{
				if (TrySerialize(item, rented.AsSpan(), out bytesWritten))
				{
					await _blobStorage.WriteBlobAsync(key, rented.AsMemory(0, bytesWritten), cancellationToken);
					Attach(item);
					return;
				}
				ArrayPool<byte>.Shared.Return(rented);
				rented = ArrayPool<byte>.Shared.Rent(rented.Length * 2);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	private bool TrySerialize(T item, Span<byte> destination, out int bytesWritten)
	{
		try
		{
			bytesWritten = 0;
			_serializer.Reset();
			_serializer.Serialize(destination, item, ref bytesWritten);
			return true;
		}
		catch (Exception ex) when (IsCapacityException(ex))
		{
			bytesWritten = 0;
			return false;
		}
	}

	private static bool IsCapacityException(Exception ex)
	{
		return ex is IndexOutOfRangeException
			|| ex is ArgumentOutOfRangeException
			|| ex is OverflowException;
	}

	private T Deserialize(ReadOnlySpan<byte> blob)
	{
		_deserializer.Reset();
		int pos = 0;
		return _deserializer.Deserialize<T>(blob, ref pos);
	}

	private void Attach(T item)
	{
		Attach(_getKeyFromItem(item), item);
	}

	private void Attach(TKey key, T item)
	{
		EventuallyMaintain();
		_attachedObjectsById[key] = new WeakReference(item);
	}

	private bool TryGetAttachedObject(TKey key, out T item)
	{
		EventuallyMaintain();
		if (_attachedObjectsById.TryGetValue(key, out var weakReference) && weakReference.Target is T attached)
		{
			item = attached;
			return true;
		}

		item = null!;
		return false;
	}

	private void EventuallyMaintain()
	{
		if (Random.Shared.Next(1024) == 0)
		{
			foreach (var deadKey in _attachedObjectsById.Where(x => !x.Value.IsAlive).Select(x => x.Key))
			{
				_attachedObjectsById.TryRemove(deadKey, out _);
			}
		}
	}
}
