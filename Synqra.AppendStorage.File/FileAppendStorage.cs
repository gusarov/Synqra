using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Synqra.BinarySerializer;

namespace Synqra.AppendStorage.File;

public class FileAppendStorage<T, TKey> : IAppendStorage<T, TKey>, IDisposable, IAsyncDisposable
{
	const int BufferSizeForObject = 10240;

	// if concurrent, then most conflicting reader vs writer, so we can have separate serializer and deserializer to avoid lock contention. But for single writer scenario, we can use the same instance.
	private readonly ISBXSerializer _serializer;
	private readonly ISBXSerializer _deserializer;
	private readonly Func<T, Guid> _getKey;

	private string _folderPath;

	public FileAppendStorage(
		IOptions<FileAppendStorageOptions> options,
		ISBXSerializerFactory serializerFactory,
		Func<T, Guid> getKey)
	{
		_serializer = serializerFactory.CreateSerializer();
		_deserializer = serializerFactory.CreateSerializer();
		_getKey = getKey;
		var path = options.Value.Folder;
		if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)) // this is a special flag that target - is directory, not potential file path. We treat this as a root folder where you actually want files. Otherwise we create "storege" subfolder and consider it is a mixed usage
		{
			_folderPath = path;
		}
		else
		{
			_folderPath = Path.Combine(path, "storage");
		}
	}

	bool _created;

	void EnsureCreated()
	{
		if (!_created)
		{
			Directory.CreateDirectory(_folderPath);
			_created = true;
		}
	}

	public Task<string> TestAsync(string input) => Task.FromResult(input);

	public Task AppendAsync(T item, CancellationToken cancellationToken = default)
	{
		AppendCore(item);
		return Task.CompletedTask;
	}

	public Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
	{
		foreach (var item in items)
		{
			AppendCore(item);
		}
		return Task.CompletedTask;
	}

	string GetFileNameFor(Guid key, bool ensureFolders = false)
	{
		int prefLen;
#if NET9_0_OR_GREATER
		if (key.Version == 7)
		{
			prefLen = 6; // 6 characters gives a new folder every 4.66 hours and gives 1880 folders a year, which is manageable for file systems and keeps folder sizes reasonable.
		}
		else if (key.Version == 6)
		{
			prefLen = 5; // 5 characters gives a new folder every 1.27 days and gives 287 folders a year, which is very manageable for file systems and keeps folder sizes reasonable.
		}
		else
#endif
		{
			prefLen = 3; // for v8 let's hope it is sha256. Otherwise need to provide ability to implement custom prefixing. Anyway, this field is for high entropy.
		}
		/*
		var colPref = Path.Combine(_folderPath, key.ToString("N").Substring(0, prefLen));
		var colPath = Path.Combine(colPref, key.ToString("N").Substring(0, prefLen)[prefLen..]);
		*/
		var objPref = Path.Combine(_folderPath, key.ToString("N").Substring(0, prefLen));
		if (ensureFolders)
		{
			Directory.CreateDirectory(objPref);
		}
		var objPath = Path.Combine(objPref, key.ToString("N").Substring(prefLen));
		return objPath;
	}

	private void AppendCore(T item)
	{
		EnsureCreated();
		// Span<byte> keyBytes = stackalloc byte[16];
		// WriteBigEndianGuid(_getKey(item), keyBytes);

		Span<byte> buffer = stackalloc byte[BufferSizeForObject];
		int pos = 0;
		_serializer.Reset();
		_serializer.Serialize(buffer, item, ref pos);

#if NET9_0_OR_GREATER
		System.IO.File.WriteAllBytes(GetFileNameFor(_getKey(item), true), buffer[..pos]);
#else
		System.IO.File.WriteAllBytes(GetFileNameFor(_getKey(item), true), buffer.ToArray());
#endif
	}

	public async IAsyncEnumerable<T> GetAllAsync(
		TKey? from = default,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var rootInfo = new DirectoryInfo(_folderPath);
		if (rootInfo.Exists)
		{
			foreach (var prefixDirectoryInfo in rootInfo.EnumerateDirectories().OrderBy(x => x.Name))
			{
				if (cancellationToken.IsCancellationRequested)
				{
					yield break;
				}
				foreach (var objectFileInfo in prefixDirectoryInfo.EnumerateFiles().OrderBy(x => x.Name))
				{
					if (cancellationToken.IsCancellationRequested)
					{
						yield break;
					}
					var blob = await System.IO.File.ReadAllBytesAsync(objectFileInfo.FullName, cancellationToken);
					_deserializer.Reset();
					int pos = 0;
					yield return _deserializer.Deserialize<T>(blob, ref pos);
				}
			}
		}
	}

	public Task FlushAsync(CancellationToken cancellationToken = default)
	{
		// WAL mode handles durability; explicit checkpoint if needed
		return Task.CompletedTask;
	}

	/*
	private T DeserializeItem(byte[] data)
	{
		int pos = 0;
		return _serializer.Deserialize<T>(data.AsSpan(), ref pos);
	}
	*/

	/// <summary>
	/// Writes Guid as big-endian 16 bytes so it preserves v7 chronological order.
	/// </summary>
	private static void WriteBigEndianGuid(Guid guid, Span<byte> bytes)
	{
		guid.TryWriteBytes(bytes);
		// .NET Guid layout on little-endian: int(LE) short(LE) short(LE) 8-bytes(BE)
		// Swap first 4 bytes
		(bytes[0], bytes[3]) = (bytes[3], bytes[0]);
		(bytes[1], bytes[2]) = (bytes[2], bytes[1]);
		// Swap bytes 4-5
		(bytes[4], bytes[5]) = (bytes[5], bytes[4]);
		// Swap bytes 6-7
		(bytes[6], bytes[7]) = (bytes[7], bytes[6]);
		// Bytes 8-15 are already big-endian
	}

	public void Dispose()
	{
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}