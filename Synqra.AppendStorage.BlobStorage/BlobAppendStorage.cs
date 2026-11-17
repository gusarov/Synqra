using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Synqra.BinarySerializer;
using Synqra.AppendStorage;
using Synqra.BlobStorage;

namespace Synqra.AppendStorage.BlobStorage;

/// <summary>
/// SBX records in a blob storage. An SBX payload names no schema versions: a reader decodes it at the versions it is configured
/// with, and a reader configured at its own latest ran past the end of every record a model had since grown (2026-11-17). Over a
/// store that keeps schemas bundles (<see cref="ITaggedBlobStorage{TKey}"/>: IndexedDB), each record carries, as a field
/// beside its pure SBX bytes, the id of the <see cref="SchemasBundle"/> it was written with; the bundle is stored once, and each
/// record is read by a deserializer configured for its bundle. A record without the field was written before bundles: its
/// versions are unknown, so it is refused by name (its log is dropped and fetched again), never guessed at. Any other store keeps
/// pure SBX read at this build's versions, as before.
/// </summary>
public class BlobAppendStorage<T, TKey> : IAppendStorage<T, TKey>, IClearableAppendStorage, ISalvageableAppendStorage<T>
	where T : class
	where TKey : notnull, IComparable<TKey>
{
	private const int StackBufferSize = 16 * 1024;
	private const int InitialHeapBufferSize = 64 * 1024;

	private readonly IBlobStorage<TKey> _blobStorage;
	private readonly IJsonMirrorBlobStorage<TKey>? _jsonMirrorBlobStorage;
	private readonly ITaggedBlobStorage<TKey>? _tagged;
	private readonly ISbxSerializerFactory _serializerFactory;
	private readonly ISbxSerializer _serializer;
	private readonly ISbxSerializer _deserializer;
	private readonly Func<T, TKey> _getKeyFromItem;
	private readonly JsonSerializerOptions? _jsonMirrorOptions;
	private readonly ConcurrentDictionary<TKey, WeakReference> _attachedObjectsById = new();
	private readonly SemaphoreSlim _bundlesGate = new SemaphoreSlim(1, 1);
	// The bundles read so far, and a deserializer configured for each: SBX is reset per record anyway.
	private volatile IReadOnlyDictionary<uint, SchemasBundle> _bundles = new Dictionary<uint, SchemasBundle>();
	private readonly ConcurrentDictionary<uint, ISbxSerializer> _readers = new();
	private bool _bundlesLoaded;
	private SchemasBundle? _writeBundle;

	public BlobAppendStorage(
		  IBlobStorage<TKey> blobStorage
		, ISbxSerializerFactory serializerFactory
		, Func<T, TKey> getKeyFromItem
		, JsonSerializerOptions? jsonMirrorOptions = null
		)
	{
		_blobStorage = blobStorage;
		_jsonMirrorBlobStorage = blobStorage as IJsonMirrorBlobStorage<TKey>;
		_serializerFactory = serializerFactory;
		_serializer = serializerFactory.CreateSerializer();
		_deserializer = serializerFactory.CreateSerializer();
		_tagged = _serializer is ISbxSchemaVersions && _deserializer is ISbxSchemaVersions ? blobStorage as ITaggedBlobStorage<TKey> : null;
		_getKeyFromItem = getKeyFromItem;
		_jsonMirrorOptions = jsonMirrorOptions;
	}

	public Task<string> TestAsync(string input) => Task.FromResult(input);

	public virtual async Task AppendAsync(T item, CancellationToken cancellationToken = default)
	{
		await RegisterSchemasBundleAsync(cancellationToken);
		if (_tagged is not null)
		{
			await _tagged.WriteBlobAsync(_getKeyFromItem(item), SerializeRecord(item), _writeBundle!.Id, JsonMirrorOf(item), cancellationToken);
		}
		else if (_blobStorage.SupportsSyncOperations)
		{
			_blobStorage.WriteBlob(_getKeyFromItem(item), SerializeRecord(item));
		}
		else
		{
			await WriteBlobAsync(_getKeyFromItem(item), SerializeRecord(item), item, cancellationToken);
		}
		Attach(item);
	}

	public virtual async Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
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

		var record = _tagged is not null
			? await _tagged.ReadTaggedBlobAsync(key, cancellationToken)
			: new TaggedBlob(await _blobStorage.ReadBlobAsync(key, cancellationToken), null);
		var item = Deserialize(key, record, await FindReaderAsync(record, salvaging: false, cancellationToken));
		Attach(key, item);
		return item;
	}

	public IAsyncEnumerable<T> GetAllAsync(TKey? from = default, CancellationToken cancellationToken = default)
		=> _tagged is not null ? ReadBundledAsync(from, cancellationToken)
			: _blobStorage is IPagedBlobReader<TKey> paged ? ReadPagedAsync(paged, from, cancellationToken)
			: ReadByKeyAsync(from, cancellationToken);

	async IAsyncEnumerable<T> ReadBundledAsync(TKey? from, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var read = 0;
		var deserialized = 0;
		long deserializing = 0;
		await foreach (var (key, record) in _tagged!.EnumerateTaggedBlobsAsync(from, cancellationToken))
		{
			read++;
			if (TryGetAttachedObject(key, out var attached))
			{
				yield return attached;
			}
			else
			{
				var reader = await FindReaderAsync(record, salvaging: false, cancellationToken);
				var started = Stopwatch.GetTimestamp();
				var item = Deserialize(key, record, reader);
				Attach(key, item);
				deserializing += Stopwatch.GetTimestamp() - started;
				deserialized++;
				yield return item;
			}
		}
		if (EmergencyLog.Default.IsStaging())
		{
			EmergencyLog.Default.Stage($"append storage {typeof(T).Name}: {read} read, {deserialized} deserialized in {Stopwatch.GetElapsedTime(0, deserializing).TotalMilliseconds:F0} ms");
		}
	}

	// A page of keys and blobs per round trip: a cold browser log of 14k events opened in minutes when read one blob at a time.
	async IAsyncEnumerable<T> ReadPagedAsync(IPagedBlobReader<TKey> paged, TKey? from, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (var (key, blob) in paged.EnumerateBlobsAsync(from, cancellationToken))
		{
			if (TryGetAttachedObject(key, out var attached))
			{
				yield return attached;
			}
			else
			{
				var item = Deserialize(key, new TaggedBlob(blob, null), (_deserializer, null));
				Attach(key, item);
				yield return item;
			}
		}
	}

	async IAsyncEnumerable<T> ReadByKeyAsync(TKey? from, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (var key in _blobStorage.EnumerateKeysAsync(from, cancellationToken))
		{
			if (TryGetAttachedObject(key, out var attached))
			{
				yield return attached;
			}
			else
			{
				var item = Deserialize(key, new TaggedBlob(await _blobStorage.ReadBlobAsync(key, cancellationToken), null), (_deserializer, null));
				Attach(key, item);
				yield return item;
			}
		}
	}

	/// <summary>
	/// Every record it can read, in key order, each one it cannot handed to <paramref name="unreadable"/>: for keeping what a log
	/// holds that its server lacks before the log is dropped, never for folding a projection. A record written before bundles is
	/// read here at this build's versions.
	/// </summary>
	public async IAsyncEnumerable<T> SalvageAsync(Action<UnreadableRecordException> unreadable, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await foreach (var (key, record) in EnumerateRecordsAsync(cancellationToken))
		{
			T? item = null;
			UnreadableRecordException? failure = null;
			try
			{
				item = Deserialize(key, record, await FindReaderAsync(record, salvaging: true, cancellationToken));
				RestoreFromKey(key, item);
			}
			catch (UnreadableRecordException ex)
			{
				failure = ex;
			}
			if (failure is null)
			{
				yield return item!;
			}
			else
			{
				unreadable(failure);
			}
		}
	}

	async IAsyncEnumerable<(TKey Key, TaggedBlob Record)> EnumerateRecordsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (_tagged is not null)
		{
			await foreach (var record in _tagged.EnumerateTaggedBlobsAsync(default, cancellationToken))
			{
				yield return record;
			}
		}
		else if (_blobStorage is IPagedBlobReader<TKey> paged)
		{
			await foreach (var (key, blob) in paged.EnumerateBlobsAsync(default, cancellationToken))
			{
				yield return (key, new TaggedBlob(blob, null));
			}
		}
		else
		{
			await foreach (var key in _blobStorage.EnumerateKeysAsync(default, cancellationToken))
			{
				yield return (key, new TaggedBlob(await _blobStorage.ReadBlobAsync(key, cancellationToken), null));
			}
		}
	}

	public Task FlushAsync(CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public async Task ClearAllAsync(CancellationToken cancellationToken = default)
	{
		if (_blobStorage is not IClearableBlobStorage clearable)
		{
			throw new NotSupportedException($"{_blobStorage.GetType().Name} does not support clearing — only implementations of IClearableBlobStorage do.");
		}
		await _bundlesGate.WaitAsync(cancellationToken);
		try
		{
			await clearable.ClearAllAsync(cancellationToken);
			// The attached-objects cache would otherwise keep serving these same in-memory
			// instances back out of GetAllAsync/GetAsync even though the backing store is empty.
			_attachedObjectsById.Clear();
			_writeBundle = null; // its bundle went with the records: the next write stores it again
			_bundlesLoaded = false;
		}
		finally
		{
			_bundlesGate.Release();
		}
	}

	public void Dispose()
	{
		_blobStorage.Dispose();
	}

	public ValueTask DisposeAsync()
	{
		return _blobStorage.DisposeAsync();
	}

	/// <summary>Puts back on a salvaged item what its record keeps only in its key.</summary>
	protected virtual void RestoreFromKey(TKey key, T item)
	{
	}

	#region Schemas bundles

	// A record's tag is the id of its schemas bundle; the bundles are the store's "schemas" side entries, keyed by id in hex.
	private const string SchemasSide = "schemas";

	string FormatBundleKey(uint bundleId) => bundleId.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>The bundle id this storage's records are written with, once registered; null over a store that keeps no tags.</summary>
	protected uint? WriteSchemasBundleId => _writeBundle?.Id;

	/// <summary>
	/// Stores this build's schemas bundle once, before its first record. A stored bundle of the same id with other content fails
	/// loudly: a record would otherwise be read at the wrong versions.
	/// </summary>
	protected async Task RegisterSchemasBundleAsync(CancellationToken cancellationToken)
	{
		if (_tagged is not null && _writeBundle is null)
		{
			var bundle = new SchemasBundle((ISbxSchemaVersions)_serializer);
			await _bundlesGate.WaitAsync(cancellationToken);
			try
			{
				await _tagged.StoreSideEntryAsync(SchemasSide, FormatBundleKey(bundle.Id), bundle.Content, cancellationToken);
				await ReadSchemasBundlesAsync(cancellationToken);
				if (!_bundles[bundle.Id].Content.AsSpan().SequenceEqual(bundle.Content))
				{
					throw new UnreadableRecordException(bundle.ToString(), bundle.Content.Length, bundle.ToString(), new InvalidOperationException("Two different schemas bundles share one id."));
				}
				_writeBundle = bundle;
			}
			finally
			{
				_bundlesGate.Release();
			}
		}
	}

	// The deserializer for the record's bundle, or null with why there is none; a bundle stored after the last read (another
	// tab's newer build) is read first.
	async ValueTask<(ISbxSerializer? Reader, string? Missing)> FindReaderAsync(TaggedBlob record, bool salvaging, CancellationToken cancellationToken)
	{
		(ISbxSerializer? Reader, string? Missing) reader;
		if (_tagged is null)
		{
			reader = (_deserializer, null);
		}
		else if (record.Tag is { } bundleId)
		{
			if (!_bundles.ContainsKey(bundleId) || !_bundlesLoaded)
			{
				await _bundlesGate.WaitAsync(cancellationToken);
				try
				{
					await ReadSchemasBundlesAsync(cancellationToken);
				}
				finally
				{
					_bundlesGate.Release();
				}
			}
			reader = _bundles.TryGetValue(bundleId, out var bundle)
				? (_readers.GetOrAdd(bundleId, _ => ConfigureReader(bundle)), null)
				: (null, FormattableString.Invariant($"Schemas bundle {bundleId:x8} is not stored here."));
		}
		else
		{
			reader = salvaging
				? (_deserializer, null)
				: (null, "The record names no schemas bundle: it was written before bundles, its versions are unknown, so the log is dropped and fetched again.");
		}
		return reader;
	}

	// Called under the gate. A stored bundle that does not match its id is refused by name.
	async Task ReadSchemasBundlesAsync(CancellationToken cancellationToken)
	{
		var bundles = new Dictionary<uint, SchemasBundle>();
		foreach (var (key, content) in await _tagged!.ReadSideEntriesAsync(SchemasSide, cancellationToken))
		{
			var id = uint.Parse(key, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
			try
			{
				bundles[id] = new SchemasBundle(id, content);
			}
			catch (InvalidDataException ex)
			{
				throw new UnreadableRecordException(FormattableString.Invariant($"schemas bundle {id:x8}"), content.Length, FormattableString.Invariant($"schemas bundle {id:x8}"), ex);
			}
		}
		_bundles = bundles;
		_bundlesLoaded = true;
	}

	ISbxSerializer ConfigureReader(SchemasBundle bundle)
	{
		var reader = _serializerFactory.CreateSerializer();
		bundle.Apply((ISbxSchemaVersions)reader);
		return reader;
	}

	#endregion

	#region Records

	// The payload from the stack when it fits, else from a pooled array that grows until it does.
	private byte[] SerializeRecord(T item)
	{
		byte[] record;
		Span<byte> stackBuffer = stackalloc byte[StackBufferSize];
		_serializer.Reset();
		if (_serializer.TrySerializeFrame(item, stackBuffer, out var bytesWritten))
		{
			record = stackBuffer[..bytesWritten].ToArray();
		}
		else
		{
			var frame = _serializer.SerializeFrame(item, ArrayPool<byte>.Shared.Rent(InitialHeapBufferSize), ArrayPool<byte>.Shared);
			try
			{
				record = frame.ToArray();
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(frame.Array!);
			}
		}
		return record;
	}

	/// <summary>
	/// SBX (<paramref name="sbxData"/>) is the value GetAsync/GetAllAsync read back — always
	/// written. When the underlying blob storage also implements
	/// <see cref="IJsonMirrorBlobStorage{TKey}"/> (currently only IndexedDb) and wants one,
	/// a JSON rendering of the same item is written into the SAME record alongside it — not
	/// a second store/key — purely for human inspection.
	/// </summary>
	private async Task WriteBlobAsync(TKey key, ReadOnlyMemory<byte> sbxData, T item, CancellationToken cancellationToken)
	{
		if (_jsonMirrorBlobStorage is { WantsJsonMirror: true })
		{
			var json = JsonSerializer.Serialize(item, item.GetType(), _jsonMirrorOptions);
			await _jsonMirrorBlobStorage.WriteBlobAsync(key, sbxData, json, cancellationToken);
		}
		else
		{
			await _blobStorage.WriteBlobAsync(key, sbxData, cancellationToken);
		}
	}

	// A failure names the record and its bundle. Caught by type, never by a filter: a filter in a generic method traps under WASM
	// AOT (see SbxFrameExtensions).
	private T Deserialize(TKey key, TaggedBlob record, (ISbxSerializer? Reader, string? Missing) reader)
	{
		T item = null!;
		Exception? failure = null;
		try
		{
			var deserializer = reader.Reader ?? throw new InvalidOperationException(reader.Missing);
			deserializer.Reset();
			var pos = 0;
			item = deserializer.Deserialize<T>(record.Blob, ref pos);
		}
		catch (Exception ex)
		{
			failure = ex;
		}
		if (failure is not null)
		{
			throw new UnreadableRecordException(key.ToString() ?? "?", record.Blob.Length, DescribeBundle(record), failure);
		}
		return item;
	}

	string? DescribeBundle(TaggedBlob record)
		=> record.Tag is { } id
			? (_bundles.TryGetValue(id, out var bundle) ? $"{bundle}: {string.Join(", ", bundle.Entries.Select(x => FormattableString.Invariant($"{x.MapId}@{x.Version}")))}" : FormattableString.Invariant($"schemas bundle {id:x8}, not stored here"))
			: null;

	#endregion

	/// <summary>The item's SBX bytes: what <see cref="GetAsync"/> and <see cref="GetAllAsync"/> read back. Register the bundle first.</summary>
	protected byte[] Serialize(T item) => SerializeRecord(item);

	/// <summary>The JSON rendering stored beside the SBX bytes, when the blob storage wants one; otherwise null.</summary>
	protected string? JsonMirrorOf(T item)
		=> _jsonMirrorBlobStorage is { WantsJsonMirror: true } ? JsonSerializer.Serialize(item, item.GetType(), _jsonMirrorOptions) : null;

	/// <summary>Keeps a written item for <see cref="GetAsync"/> and <see cref="GetAllAsync"/> to hand back without reading it again.</summary>
	protected void Attach(T item)
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
