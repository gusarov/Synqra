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

	// Append-order manifest: one hex key per line, written in actual WriteBlob call order.
	// File timestamps and the key itself are NOT reliable for replay ordering — see the
	// remarks on EnumerateFilesRecursive. Lives at the store root (not inside the sharded
	// tree) under a name EnumerateFilesRecursive explicitly skips so it's never mistaken
	// for a blob.
	private const string ManifestFileName = "_order.idx";
	private readonly object _manifestLock = new();
	private string ManifestPath => Path.Combine(_folderPath, ManifestFileName);

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

	public async ValueTask WriteBlobAsync(TKey key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
	{
		EnsureCreated();
		var keyHex = _getPathFromKey(key);
		var fileName = GetFileNameFor(keyHex, create: true);
#if NET9_0_OR_GREATER
		await System.IO.File.WriteAllBytesAsync(fileName, blob, cancellationToken);
#else
		await System.IO.File.WriteAllBytesAsync(fileName, blob.ToArray(), cancellationToken);
#endif
		AppendToManifest(keyHex);
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

		bool Matches(string keyHex) => string.IsNullOrEmpty(fromKey) || keyHex.StartsWith(fromKey, StringComparison.Ordinal);

		var seen = new HashSet<string>(StringComparer.Ordinal);
		var manifestPath = ManifestPath;
		if (System.IO.File.Exists(manifestPath))
		{
			// Authoritative order: actual WriteBlob call order, not filename/timestamp —
			// see the manifest field's own remarks.
			foreach (var line in System.IO.File.ReadLines(manifestPath))
			{
				if (cancellationToken.IsCancellationRequested)
				{
					yield break;
				}

				var keyHex = line.Trim();
				if (keyHex.Length == 0 || !seen.Add(keyHex))
				{
					continue;
				}

				// The manifest is append-only and never compacted on delete — confirm the
				// blob still exists before trusting an entry.
				if (!System.IO.File.Exists(GetFileNameFor(keyHex, create: false)))
				{
					continue;
				}

				if (Matches(keyHex))
				{
					yield return _getKeyFromPath(keyHex);
				}
			}
		}

		// Fallback, legacy-order pass: any blob on disk the manifest doesn't mention —
		// either written before this manifest existed, or (best-effort) a write whose
		// manifest append failed/raced. Nothing silently disappears; it just sorts after
		// every manifest-ordered entry, in the old alphabetical-by-key order.
		foreach (var fileInfo in EnumerateFilesRecursive(rootInfo))
		{
			if (cancellationToken.IsCancellationRequested)
			{
				yield break;
			}

			var keyHex = GetKeyHexFromPath(fileInfo.FullName);
			if (!seen.Add(keyHex))
			{
				continue;
			}

			if (Matches(keyHex))
			{
				yield return _getKeyFromPath(keyHex);
			}
		}

		await Task.CompletedTask;
	}

	/// <summary>
	/// Only used as a fallback for blobs the manifest (see <see cref="ManifestPath"/>)
	/// doesn't mention. Filename/timestamp ordering is NOT reliable for replay order in
	/// general — filename is (the rest of) a v7 GUID key, and Guid.CreateVersion7()'s
	/// sub-millisecond bits are cryptographically random, not a monotonic counter, so two
	/// blobs written milliseconds apart (e.g. during a bulk seed loop) can sort in the
	/// WRONG order alphabetically; file timestamps were tried too and found imprecise for
	/// the same tight-loop case on this filesystem. CreationTimeUtc here is still strictly
	/// better than nothing for this best-effort fallback path.
	/// </summary>
	private IEnumerable<FileInfo> EnumerateFilesRecursive(DirectoryInfo directoryInfo)
	{
		foreach (var objectFileInfo in directoryInfo.EnumerateFiles()
			.Where(x => x.Name != ManifestFileName)
			.OrderBy(x => x.CreationTimeUtc)
			.ThenBy(x => x.Name, StringComparer.Ordinal))
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
		var keyHex = _getPathFromKey(key);
		var fileName = GetFileNameFor(keyHex, create: true);

#if NET9_0_OR_GREATER
		System.IO.File.WriteAllBytes(fileName, blob);
#else
		System.IO.File.WriteAllBytes(fileName, blob.ToArray());
#endif
		AppendToManifest(keyHex);
	}

	private void AppendToManifest(string keyHex)
	{
		lock (_manifestLock)
		{
			System.IO.File.AppendAllText(ManifestPath, keyHex + "\n");
		}
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
