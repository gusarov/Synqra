using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synqra.BinarySerializer;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Synqra.AppendStorage.File;

public class FileAppendStorage<T, TKey> : IAppendStorage<T, TKey>, IDisposable, IAsyncDisposable
	where T : class
	where TKey : notnull
{
	const int BufferSizeForObject = 10240;

	// if concurrent, then most conflicting reader vs writer, so we can have separate serializer and deserializer to avoid lock contention. But for single writer scenario, we can use the same instance.
	private readonly ISBXSerializer _serializer;
	private readonly ISBXSerializer _deserializer;
	private readonly Func<T, TKey> _getKeyFromItem;
	private readonly Func<TKey, string> _getPathFromKey;
	private readonly Func<string, TKey> _getKeyFromPath;
	private readonly JsonSerializerOptions _jsonSerializerOptions;
	private readonly ConcurrentDictionary<TKey, WeakReference> _attachedObjectsById = new();

	private string _folderPath;

	public FileAppendStorage(
		  IOptions<FileAppendStorageOptions> options
		, ISBXSerializerFactory serializerFactory
		, JsonSerializerOptions jsonSerializerOptions
		, Func<T, TKey> getKeyFromItem
		, Func<TKey, string> getPathFromKey
		, Func<string, TKey> getKeyFromPath
		)
	{
		_serializer = serializerFactory.CreateSerializer();
		_deserializer = serializerFactory.CreateSerializer();
		_jsonSerializerOptions = new JsonSerializerOptions(jsonSerializerOptions) { WriteIndented = true };
		_getKeyFromItem = getKeyFromItem;
		_getPathFromKey = getPathFromKey;
		_getKeyFromPath = getKeyFromPath;
		var path = options.Value.Folder;
		if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)) // this is a special flag that target - is directory, not potential file path. We treat this as a root folder where you actually want files. Otherwise we create "storege" subfolder and consider it is a mixed usage
		{
			_folderPath = path;
		}
		else
		{
			_folderPath = Path.Combine(path, "storage");
		}
		_folderPath = _folderPath.Replace("[Type]", typeof(T).Name);
	}

	T? GetObject(TKey key)
	{
		EventuallyMaintain();
		if (_attachedObjectsById.TryGetValue(key, out var item))
		{
			return item.Target as T;
		}
		return null;
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

	private void AppendCore(T item)
	{
		EnsureCreated();
		// Span<byte> keyBytes = stackalloc byte[16];
		// WriteBigEndianGuid(_getKey(item), keyBytes);

		EventuallyMaintain();
		var key = _getKeyFromItem(item);
		if (_attachedObjectsById.TryGetValue(key, out var wr))
		{
			var target = wr.Target;
			if (target != null && ReferenceEquals(target, item))
			{
				throw new Exception("Another object already tracked by AppendStorage, it is not new and not same!");
			}
		}
		_attachedObjectsById[key] = new WeakReference(item);

		Span<byte> buffer = stackalloc byte[BufferSizeForObject];
		int pos = 0;
		_serializer.Reset();
		_serializer.Serialize(buffer, item, ref pos);

#if DEBUG
		try
		{
			var json = JsonSerializer.SerializeToUtf8Bytes<object>(item, _jsonSerializerOptions); ;
			json.CopyTo(buffer[pos..]);
			pos += json.Length;
		}
		catch (Exception ex)
		{
			var buf2 = ex.ToString().Utf8();
			buf2.CopyTo(buffer[pos..]);
			pos += buf2.Length;
		}
#endif

		var path = GetFileNameFor(_getPathFromKey(key), true);
		System.IO.File.WriteAllBytes(path
#if NET9_0_OR_GREATER
		, buffer[..pos]
#else
		, buffer.ToArray()
#endif
		);
	}

	internal string GetFileNameFor(string key, bool create)
	{
		var origKey = key;
		if (!key.Contains(Path.DirectorySeparatorChar))
		{
			key = GetFileNameForRec(key); // auto spread layout
		}
		key = Path.Combine(_folderPath, key);
		EmergencyLog.Default.LogDebug("GetFileNameFor: " + origKey + " -> " + key);
		if (create)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(key));
		}
		return key;
	}

	internal static string GetFileNameForRec(string key)
	{
		int prefLen;

		// assume first 32 characters are - GUID HEX value (Big Endian, Network, same as ToStrint("N"), not native LE memory map)
		if (key.Length >= 32 && Guid.TryParse(key[..32], out var g))
		{
			if (g.GetVersion() == 7)
			{
				prefLen = 6; // 6 characters gives a new folder every 4.66 hours and gives 1880 folders a year, which is manageable for file systems and keeps folder sizes reasonable.
			}
			else if (g.GetVersion() == 6)
			{
				prefLen = 5; // 5 characters gives a new folder every 1.27 days and gives 287 folders a year, which is very manageable for file systems and keeps folder sizes reasonable.
			}
			else
			{
				prefLen = 3; // for v8 let's hope it is sha256. Otherwise need to provide ability to implement custom prefixing. Anyway, this field is for high entropy.
			}
			var objPref = key[..prefLen];
			var objPath = Path.Combine(objPref, key[prefLen..32]);
			if (key.Length <= 32)
			{
				return objPath;
			}
			else
			{
				objPath = Path.Combine(objPath, GetFileNameForRec(key[32..]));
				return objPath;
			}
		}
		else
		{
			prefLen = 2; // assume high ASCII entropy
		}
		{
			/*
			var colPref = Path.Combine(_folderPath, key.ToString("N").Substring(0, prefLen));
			var colPath = Path.Combine(colPref, key.ToString("N").Substring(0, prefLen)[prefLen..]);
			*/
			var objPref = key[..prefLen];
			var objPath = Path.Combine(objPref, key[prefLen..]);
			return objPath;
		}
	}

	public async Task<T> GetAsync(
		  TKey key
		, CancellationToken cancellationToken = default
		)
	{
		var cached = GetObject(key);
		if (cached != null)
		{
			return cached;
		}
		var rootInfo = new DirectoryInfo(_folderPath);
		if (!rootInfo.Exists)
		{
			throw new Exception("Object is not found");
		}
		var fileName = GetFileNameFor(_getPathFromKey(key), false);
		if (!System.IO.File.Exists(fileName))
		{
			throw new Exception("Object is not found");
		}

		if (_attachedObjectsById.TryGetValue(key, out var obj))
		{
			return (T)obj.Target!;
		}

		var buf = System.IO.File.ReadAllBytes(fileName);
		_serializer.Reset();
		int pos = 0;
		var des = _serializer.Deserialize<T>(buf, ref pos);
		_attachedObjectsById[key] = new WeakReference(des);
		return des;
	}

	public async IAsyncEnumerable<T> GetAllAsync(
		  TKey? from = default
		, [EnumeratorCancellation] CancellationToken cancellationToken = default
		)
	{
		EmergencyLog.Default.LogDebug("<GetAllAsync>");
		var rootInfo = new DirectoryInfo(_folderPath);
		if (rootInfo.Exists)
		{
			string? fromKey;
			if (from != null)
			{
				if (!Equals(from, default(TKey)))
				{
					fromKey = _getPathFromKey(from);
					if (fromKey.Length == 64 && fromKey.TrimEnd(['0']).Length <= 32)
					{
						fromKey = fromKey[..32];
					}
				}
				else
				{
					fromKey = null;
				}
			}
			else
			{
				fromKey = null;
			}
			await foreach (var item in EnumerateDirectoryRecursiveAsync(
				  rootInfo
				, requiredPrefix: fromKey
				, prefix: ""
				, cancellationToken))
			{
				yield return item;
				EmergencyLog.Default.LogDebug("GetAllAsync: " + item);
			}
		}
		EmergencyLog.Default.LogDebug("</GetAllAsync>");
	}

	private async IAsyncEnumerable<T> EnumerateDirectoryRecursiveAsync(
		DirectoryInfo directoryInfo,
		string requiredPrefix = "",
		string prefix = "",
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		EmergencyLog.Default.LogDebug("<EnumerateDirectoryRecursiveAsync> " + directoryInfo.FullName);

		var srch = "*";
		var prefix2 = "";

		if (!string.IsNullOrEmpty(requiredPrefix))
		{
			var path = GetFileNameFor(requiredPrefix, false);
			if (directoryInfo.FullName.Length <= path.Length)
			{
				path = path[directoryInfo.FullName.Length..].TrimStart(Path.DirectorySeparatorChar);
				if (!string.IsNullOrEmpty(path))
				{
					while (path.Contains(Path.DirectorySeparatorChar))
					{
						path = Path.GetDirectoryName(path);
					}
					srch = path + "*";
					prefix2 = path;
				}
			}
		}

		// Yield files in current directory first, ordered by name
		foreach (var objectFileInfo in directoryInfo.EnumerateFiles(srch).OrderBy(x => x.Name))
		{
			if (cancellationToken.IsCancellationRequested)
			{
				yield break;
			}
			EmergencyLog.Default.LogDebug("EnumerateDirectoryRecursiveAsync: file " + objectFileInfo.FullName);

			var keyHex = objectFileInfo.FullName.Substring(_folderPath.Length).Replace(Path.DirectorySeparatorChar + "", "");
			var key = _getKeyFromPath(keyHex);
			EventuallyMaintain();
			bool hit = false;
			if (_attachedObjectsById.TryGetValue(key, out var wr))
			{
				var target = wr.Target;
				if (target != null)
				{
					EmergencyLog.Default.LogDebug("EnumerateDirectoryRecursiveAsync: hit attached object for key " + keyHex);
					yield return (T)target;
					hit = true;
				}
			}
			if (!hit)
			{
				var blob = await System.IO.File.ReadAllBytesAsync(objectFileInfo.FullName, cancellationToken);
				_deserializer.Reset();
				int pos = 0;
				var item = _deserializer.Deserialize<T>(blob, ref pos);
				_attachedObjectsById[key] = new WeakReference(item);
				EmergencyLog.Default.LogDebug("EnumerateDirectoryRecursiveAsync: deserialized for key " + keyHex);
				yield return item;
			}
		}

		// Recurse into subdirectories, ordered by name
		foreach (var subDirectoryInfo in directoryInfo.EnumerateDirectories(srch).OrderBy(x => x.Name))
		{
			if (cancellationToken.IsCancellationRequested)
			{
				yield break;
			}
			await foreach (var item in EnumerateDirectoryRecursiveAsync(subDirectoryInfo, requiredPrefix, prefix2, cancellationToken))
			{
				yield return item;
			}
		}
		EmergencyLog.Default.LogDebug("</EnumerateDirectoryRecursiveAsync>");
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