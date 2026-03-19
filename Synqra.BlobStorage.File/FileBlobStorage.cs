using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using Synqra.BlobStorage;

namespace Synqra.BlobStorage.File;

public class FileBlobStorage<TKey> : IBlobStorage<TKey>
	where TKey : notnull, IComparable<TKey>
{
	private readonly Func<TKey, string> _getPathFromKey;
	private readonly Func<string, TKey> _getKeyFromPath;
	private readonly string _folderPath;
	private bool _created;

	public FileBlobStorage(
		  FileBlobStorageOptions options
		, string storeName
		, Func<TKey, string> getPathFromKey
		, Func<string, TKey> getKeyFromPath
		)
	{
		_getPathFromKey = getPathFromKey;
		_getKeyFromPath = getKeyFromPath;
		_folderPath = ResolveFolder(options.Folder, storeName);
	}

	public bool SupportsSyncOperations => true;

	private static string ResolveFolder(string rootFolder, string storeName)
	{
		var path = string.IsNullOrWhiteSpace(rootFolder)
			? Path.Combine("storage", "[Store]")
			: rootFolder;

		if (path.Contains("[Store]", StringComparison.Ordinal))
		{
			path = path.Replace("[Store]", storeName, StringComparison.Ordinal);
		}
		else
		{
			path = Path.Combine(path, storeName);
		}

		return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}

	private void EnsureCreated()
	{
		if (_created)
		{
			return;
		}

		Directory.CreateDirectory(_folderPath);
		_created = true;
	}

	public ValueTask<byte[]> ReadBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		var fileName = GetFileNameFor(_getPathFromKey(key), create: false);
		if (!System.IO.File.Exists(fileName))
		{
			throw new FileNotFoundException("Blob is not found", fileName);
		}

		return ValueTask.FromResult(System.IO.File.ReadAllBytes(fileName));
	}

	public ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
	{
		EnsureCreated();
		var fileName = GetFileNameFor(_getPathFromKey(key), create: true);
#if NET9_0_OR_GREATER
		return new ValueTask(System.IO.File.WriteAllBytesAsync(fileName, blob, cancellationToken));
#else
		return new ValueTask(System.IO.File.WriteAllBytesAsync(fileName, blob.ToArray(), cancellationToken));
#endif
	}

	public ValueTask DeleteBlobAsync(TKey key, CancellationToken cancellationToken = default)
	{
		var fileName = GetFileNameFor(_getPathFromKey(key), create: false);
		if (System.IO.File.Exists(fileName))
		{
			System.IO.File.Delete(fileName);
		}

		return ValueTask.CompletedTask;
	}

	public async IAsyncEnumerable<TKey> EnumerateKeysAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var rootInfo = new DirectoryInfo(_folderPath);
		if (!rootInfo.Exists)
		{
			yield break;
		}

		string? fromKey = null;
		if (from != null && !Equals(from, default(TKey)))
		{
			fromKey = _getPathFromKey(from);
			if (fromKey.Length == 64 && fromKey.TrimEnd('0').Length <= 32)
			{
				fromKey = fromKey[..32];
			}
		}

		foreach (var fileInfo in EnumerateFilesRecursive(rootInfo))
		{
			if (cancellationToken.IsCancellationRequested)
			{
				yield break;
			}

			var keyHex = GetKeyHexFromPath(fileInfo.FullName);
			if (!string.IsNullOrEmpty(fromKey) && !keyHex.StartsWith(fromKey, StringComparison.Ordinal))
			{
				continue;
			}

			yield return _getKeyFromPath(keyHex);
		}

		await Task.CompletedTask;
	}

	private IEnumerable<FileInfo> EnumerateFilesRecursive(DirectoryInfo directoryInfo)
	{
		foreach (var objectFileInfo in directoryInfo.EnumerateFiles().OrderBy(x => x.Name, StringComparer.Ordinal))
		{
			yield return objectFileInfo;
		}

		foreach (var subDirectoryInfo in directoryInfo.EnumerateDirectories().OrderBy(x => x.Name, StringComparer.Ordinal))
		{
			foreach (var nested in EnumerateFilesRecursive(subDirectoryInfo))
			{
				yield return nested;
			}
		}
	}

	private string GetKeyHexFromPath(string fullPath)
	{
		var relative = fullPath.Substring(_folderPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		relative = relative.Replace(Path.DirectorySeparatorChar.ToString(), string.Empty);
		relative = relative.Replace(Path.AltDirectorySeparatorChar.ToString(), string.Empty);
		return relative;
	}

	internal string GetFileNameFor(string key, bool create)
	{
		var originalKey = key;
		if (!key.Contains(Path.DirectorySeparatorChar) && !key.Contains(Path.AltDirectorySeparatorChar))
		{
			key = GetFileNameForRec(key);
		}

		key = Path.Combine(_folderPath, key);
		EmergencyLog.Default.LogDebug("GetFileNameFor: " + originalKey + " -> " + key);
		if (create)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(key)!);
		}

		return key;
	}

	public static string GetFileNameForRec(string key)
	{
		int prefLen;
		if (key.Length >= 32 && Guid.TryParse(key[..32], out var guid))
		{
			if (guid.GetVersion() == 7)
			{
				prefLen = 6;
			}
			else if (guid.GetVersion() == 6)
			{
				prefLen = 5;
			}
			else
			{
				prefLen = 3;
			}

			var objPref = key[..prefLen];
			var objPath = Path.Combine(objPref, key[prefLen..32]);
			if (key.Length <= 32)
			{
				return objPath;
			}

			return Path.Combine(objPath, GetFileNameForRec(key[32..]));
		}

		prefLen = 2;
		var prefix = key[..prefLen];
		return Path.Combine(prefix, key[prefLen..]);
	}

	public void WriteBlob(TKey key, ReadOnlySpan<byte> blob)
	{
		EnsureCreated();
		var fileName = GetFileNameFor(_getPathFromKey(key), create: true);

#if NET9_0_OR_GREATER
		System.IO.File.WriteAllBytes(fileName, blob);
#else
		System.IO.File.WriteAllBytes(fileName, blob.ToArray());
#endif
	}

	public void DeleteBlob(TKey key)
	{
		var fileName = GetFileNameFor(_getPathFromKey(key), create: false);
		if (System.IO.File.Exists(fileName))
		{
			System.IO.File.Delete(fileName);
		}
	}

	public void Dispose()
	{
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}
