using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Synqra.BinarySerializer;
using Synqra.AppendStorage;
using Synqra.BlobStorage;

namespace Synqra.AppendStorage.BlobStorage;

/// <summary>
/// SBX records in a blob storage. The SBX payload carries no schema versions: a reader decodes it at the versions it is told, and
/// without being told it used its own latest, so a record written before a model gained members ran past its end (2026-11-17).
/// A record therefore names what it was written with:
/// <list type="bullet">
/// <item><c>00 02</c> + a 4-byte schema set id + payload, where the storage keeps schema sets (<see cref="SchemaSetKeys{TKey}"/>):
/// a set is a build's whole table of map id → schema version, stored once beside the records and named by its content hash.</item>
/// <item><c>00 01</c> + the versions of the map ids the payload uses + payload, where it does not.</item>
/// <item>The bare payload: a record written before either. A storage that keeps sets refuses it (its log is dropped once and
/// fetched again); one that does not reads it at its latest versions, as before.</item>
/// </list>
/// A payload never starts with <c>00</c> then <c>01</c> or <c>02</c>: its first byte is the type id, and type id 0 (unknown) is
/// followed by a type name.
/// </summary>
public class BlobAppendStorage<T, TKey> : IAppendStorage<T, TKey>, IClearableAppendStorage, ISalvageableAppendStorage<T>
	where T : class
	where TKey : notnull, IComparable<TKey>
{
	private const int StackBufferSize = 16 * 1024;
	private const int InitialHeapBufferSize = 64 * 1024;
	private const byte FormatMarker = 0x00;
	private const byte InlineVersionsFormat = 0x01;
	private const byte SchemaSetFormat = 0x02;
	private const int SchemaSetIdSize = 4; // the set id's width in every record: 6 bytes of header per record in all

	private readonly IBlobStorage<TKey> _blobStorage;
	private readonly IJsonMirrorBlobStorage<TKey>? _jsonMirrorBlobStorage;
	private readonly ISbxSerializer _serializer;
	private readonly ISbxSerializer _deserializer;
	private readonly ISbxSchemaVersions? _writeVersions;
	private readonly ISbxSchemaVersions? _readVersions;
	private readonly SchemaSetKeys<TKey>? _setKeys;
	private readonly Func<T, TKey> _getKeyFromItem;
	private readonly JsonSerializerOptions? _jsonMirrorOptions;
	private readonly ConcurrentDictionary<TKey, WeakReference> _attachedObjectsById = new();
	private readonly SemaphoreSlim _setsGate = new SemaphoreSlim(1, 1);
	// The sets read so far, by id: each one's entries, and those this build reads at another version than the set names.
	private volatile IReadOnlyDictionary<uint, SchemaSet> _sets = new Dictionary<uint, SchemaSet>();
	private bool _setsLoaded;
	private uint? _writeSetId;

	public BlobAppendStorage(
		  IBlobStorage<TKey> blobStorage
		, ISbxSerializerFactory serializerFactory
		, Func<T, TKey> getKeyFromItem
		, JsonSerializerOptions? jsonMirrorOptions = null
		, SchemaSetKeys<TKey>? schemaSetKeys = null
		)
	{
		_blobStorage = blobStorage;
		_jsonMirrorBlobStorage = blobStorage as IJsonMirrorBlobStorage<TKey>;
		_serializer = serializerFactory.CreateSerializer();
		_deserializer = serializerFactory.CreateSerializer();
		_writeVersions = _serializer as ISbxSchemaVersions;
		_readVersions = _deserializer as ISbxSchemaVersions;
		_setKeys = _writeVersions is not null && _readVersions is not null ? schemaSetKeys : null;
		_getKeyFromItem = getKeyFromItem;
		_jsonMirrorOptions = jsonMirrorOptions;
	}

	public Task<string> TestAsync(string input) => Task.FromResult(input);

	public virtual async Task AppendAsync(T item, CancellationToken cancellationToken = default)
	{
		await RegisterSchemaSetAsync(cancellationToken);
		if (_blobStorage.SupportsSyncOperations)
		{
			_blobStorage.WriteBlob(_getKeyFromItem(item), SerializeRecord(item));
			Attach(item);
		}
		else
		{
			await WriteBlobAsync(_getKeyFromItem(item), SerializeRecord(item), item, cancellationToken);
			Attach(item);
		}
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

		var blob = await _blobStorage.ReadBlobAsync(key, cancellationToken);
		await PrepareAsync(blob, cancellationToken);
		var item = Deserialize(key, blob, salvaging: false);
		Attach(key, item);
		return item;
	}

	public IAsyncEnumerable<T> GetAllAsync(TKey? from = default, CancellationToken cancellationToken = default)
		=> _blobStorage is IPagedBlobReader<TKey> paged ? ReadPagedAsync(paged, from, cancellationToken) : ReadByKeyAsync(from, cancellationToken);

	// A page of keys and blobs per round trip: a cold browser log of 14k events opened in minutes when read one blob at a time.
	async IAsyncEnumerable<T> ReadPagedAsync(IPagedBlobReader<TKey> paged, TKey? from, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var read = 0;
		var deserialized = 0;
		long deserializing = 0;
		await LoadSchemaSetsAsync(cancellationToken);
		await foreach (var (key, blob) in paged.EnumerateBlobsAsync(from, cancellationToken))
		{
			if (IsSetKey(key))
			{
				// a schema set, not a record
			}
			else if (TryGetAttachedObject(key, out var attached))
			{
				read++;
				yield return attached;
			}
			else
			{
				read++;
				await PrepareAsync(blob, cancellationToken);
				var started = Stopwatch.GetTimestamp();
				var item = Deserialize(key, blob, salvaging: false);
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

	async IAsyncEnumerable<T> ReadByKeyAsync(TKey? from, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await LoadSchemaSetsAsync(cancellationToken);
		await foreach (var key in _blobStorage.EnumerateKeysAsync(from, cancellationToken))
		{
			if (IsSetKey(key))
			{
				// a schema set, not a record
			}
			else if (TryGetAttachedObject(key, out var attached))
			{
				yield return attached;
			}
			else
			{
				var blob = await _blobStorage.ReadBlobAsync(key, cancellationToken);
				await PrepareAsync(blob, cancellationToken);
				var item = Deserialize(key, blob, salvaging: false);
				Attach(key, item);
				yield return item;
			}
		}
	}

	/// <summary>
	/// Every record it can read, in key order, each one it cannot handed to <paramref name="unreadable"/>: for keeping what a log
	/// holds that its server lacks before the log is dropped, never for folding a projection. A record written before schema sets is
	/// read here at this build's versions.
	/// </summary>
	public async IAsyncEnumerable<T> SalvageAsync(Action<UnreadableRecordException> unreadable, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		UnreadableRecordException? setsFailure = null;
		try
		{
			await LoadSchemaSetsAsync(cancellationToken);
		}
		catch (UnreadableRecordException ex)
		{
			setsFailure = ex; // the records of an unreadable set fail below, each by name
		}
		if (setsFailure is not null)
		{
			unreadable(setsFailure);
		}
		await foreach (var (key, blob) in EnumerateBlobsAsync(cancellationToken))
		{
			T? item = null;
			UnreadableRecordException? failure = null;
			try
			{
				await PrepareAsync(blob, cancellationToken);
				item = Deserialize(key, blob, salvaging: true);
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

	async IAsyncEnumerable<(TKey Key, byte[] Blob)> EnumerateBlobsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (_blobStorage is IPagedBlobReader<TKey> paged)
		{
			await foreach (var record in paged.EnumerateBlobsAsync(default, cancellationToken))
			{
				if (!IsSetKey(record.Key))
				{
					yield return record;
				}
			}
		}
		else
		{
			await foreach (var key in _blobStorage.EnumerateKeysAsync(default, cancellationToken))
			{
				if (!IsSetKey(key))
				{
					yield return (key, await _blobStorage.ReadBlobAsync(key, cancellationToken));
				}
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
		await _setsGate.WaitAsync(cancellationToken);
		try
		{
			await clearable.ClearAllAsync(cancellationToken);
			// The attached-objects cache would otherwise keep serving these same in-memory
			// instances back out of GetAllAsync/GetAsync even though the backing store is empty.
			_attachedObjectsById.Clear();
			_sets = new Dictionary<uint, SchemaSet>(); // the sets went with the records: the next write stores its own again
			_setsLoaded = false;
			_writeSetId = null;
		}
		finally
		{
			_setsGate.Release();
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

	/// <summary>Whether the key holds a schema set rather than a record.</summary>
	protected bool IsSetKey(TKey key) => _setKeys?.IsSetKey(key) == true;

	#region Schema sets

	/// <summary>
	/// This build's schema set, stored once before its first record: its id is the hash of its content, so every tab of one build
	/// names the same set and a build with unchanged models reuses the one there. A stored set of that id with other content fails
	/// loudly: a record would otherwise be read at the wrong versions.
	/// </summary>
	protected async Task RegisterSchemaSetAsync(CancellationToken cancellationToken)
	{
		if (_setKeys is not null && _writeSetId is null)
		{
			var content = DescribeSchemaSet(_writeVersions!.MappedSchemaVersions);
			var setId = HashSchemaSet(content);
			var key = _setKeys.CreateKey(setId);
			await LoadSchemaSetsAsync(cancellationToken);
			await _setsGate.WaitAsync(cancellationToken);
			try
			{
				if (!_sets.ContainsKey(setId))
				{
					var framed = new byte[SchemaSetIdSize + content.Length];
					BinaryPrimitives.WriteUInt32BigEndian(framed, setId);
					content.CopyTo(framed, SchemaSetIdSize);
					await StoreSchemaSetAsync(key, framed, cancellationToken);
					var stored = await _blobStorage.ReadBlobAsync(key, cancellationToken); // another tab may have stored it first
					AddSchemaSet(key, stored);
				}
				if (!_sets[setId].Content.AsSpan().SequenceEqual(content))
				{
					throw new UnreadableRecordException(key.ToString() ?? "?", content.Length, FormattableString.Invariant($"schema set {setId:x8}"), new InvalidOperationException("Two different schema sets share one id."));
				}
				_writeSetId = setId;
			}
			finally
			{
				_setsGate.Release();
			}
		}
	}

	/// <summary>Stores a schema set under its key; a storage several writers share stores it once, whoever is first.</summary>
	protected virtual async Task StoreSchemaSetAsync(TKey key, byte[] content, CancellationToken cancellationToken)
	{
		await _blobStorage.WriteBlobAsync(key, content, cancellationToken);
	}

	async Task LoadSchemaSetsAsync(CancellationToken cancellationToken)
	{
		if (_setKeys is not null && !_setsLoaded)
		{
			await _setsGate.WaitAsync(cancellationToken);
			try
			{
				if (!_setsLoaded)
				{
					await ReadSchemaSetsAsync(cancellationToken);
					_setsLoaded = true;
				}
			}
			finally
			{
				_setsGate.Release();
			}
		}
	}

	// A record of a set stored after the sets were read (another tab's newer build) reads them again first.
	async ValueTask PrepareAsync(byte[] blob, CancellationToken cancellationToken)
	{
		if (_setKeys is not null && blob.Length >= 2 + SchemaSetIdSize && blob[0] == FormatMarker && blob[1] == SchemaSetFormat && !_sets.ContainsKey(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(2))))
		{
			await _setsGate.WaitAsync(cancellationToken);
			try
			{
				await ReadSchemaSetsAsync(cancellationToken);
			}
			finally
			{
				_setsGate.Release();
			}
		}
	}

	// Sets sort before every record: the read stops at the first key that is not one.
	async Task ReadSchemaSetsAsync(CancellationToken cancellationToken)
	{
		await using var keys = _blobStorage.EnumerateKeysAsync(default, cancellationToken).GetAsyncEnumerator(cancellationToken);
		var more = await keys.MoveNextAsync();
		while (more && _setKeys!.IsSetKey(keys.Current))
		{
			AddSchemaSet(keys.Current, await _blobStorage.ReadBlobAsync(keys.Current, cancellationToken));
			more = await keys.MoveNextAsync();
		}
	}

	// A set is its id, then its entries; one whose entries do not hash to its id is refused, never guessed at.
	void AddSchemaSet(TKey key, byte[] stored)
	{
		var setId = stored.Length >= SchemaSetIdSize ? BinaryPrimitives.ReadUInt32BigEndian(stored) : 0;
		var content = stored.Length >= SchemaSetIdSize ? stored.AsSpan(SchemaSetIdSize).ToArray() : [];
		if (stored.Length < SchemaSetIdSize || HashSchemaSet(content) != setId)
		{
			throw new UnreadableRecordException(key.ToString() ?? "?", stored.Length, FormattableString.Invariant($"schema set {setId:x8}"), new InvalidOperationException("The schema set does not match its id."));
		}
		else if (!_sets.ContainsKey(setId))
		{
			var entries = ReadSchemaSet(content);
			var remaps = entries.Where(x => _readVersions!.FindSchemaVersion(x.MapId) is { } current && current != x.Version).ToArray();
			_sets = new Dictionary<uint, SchemaSet>(_sets) { [setId] = new SchemaSet(content, entries, remaps) };
		}
	}

	// The entry count, then per map id (ascending) the id as a zigzag varint and its version as a little-endian float.
	byte[] DescribeSchemaSet(IReadOnlyCollection<KeyValuePair<int, float>> versions)
	{
		var sorted = versions.OrderBy(x => x.Key).ToArray();
		var buffer = new byte[5 + sorted.Length * 9];
		var pos = 0;
		WriteVarint(buffer, ref pos, (uint)sorted.Length);
		foreach (var (mapId, version) in sorted)
		{
			WriteVarint(buffer, ref pos, ZigZag(mapId));
			BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(pos), version);
			pos += 4;
		}
		return buffer.AsSpan(0, pos).ToArray();
	}

	(int MapId, float Version)[] ReadSchemaSet(ReadOnlySpan<byte> content)
	{
		var pos = 0;
		var count = ReadVarint(content, ref pos);
		var entries = new (int MapId, float Version)[count];
		for (var i = 0; i < count; i++)
		{
			var mapId = ReadMapId(content, ref pos);
			entries[i] = (mapId, BinaryPrimitives.ReadSingleLittleEndian(content[pos..]));
			pos += 4;
		}
		return entries;
	}

	// FNV-1a, 32 bits: the set's id.
	uint HashSchemaSet(ReadOnlySpan<byte> content)
	{
		var hash = 2166136261u;
		foreach (var b in content)
		{
			hash = (hash ^ b) * 16777619u;
		}
		return hash;
	}

	/// <summary>A stored set: its content (what its id hashes), its entries, and the entries this build reads at another version.</summary>
	sealed record SchemaSet(byte[] Content, (int MapId, float Version)[] Entries, (int MapId, float Version)[] Remaps);

	#endregion

	#region Records

	// The payload from the stack when it fits, else from a pooled array that grows until it does, behind its header.
	private byte[] SerializeRecord(T item)
	{
		byte[] record;
		Span<byte> stackBuffer = stackalloc byte[StackBufferSize];
		_serializer.Reset();
		_writeVersions?.ForgetUsage();
		if (_serializer.TrySerializeFrame(item, stackBuffer, out var bytesWritten))
		{
			record = FrameRecord(stackBuffer[..bytesWritten]);
		}
		else
		{
			var frame = _serializer.SerializeFrame(item, ArrayPool<byte>.Shared.Rent(InitialHeapBufferSize), ArrayPool<byte>.Shared);
			try
			{
				record = FrameRecord(frame.AsSpan());
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(frame.Array!);
			}
		}
		return record;
	}

	private byte[] FrameRecord(ReadOnlySpan<byte> payload)
	{
		byte[] record;
		if (_writeSetId is { } setId)
		{
			record = new byte[2 + SchemaSetIdSize + payload.Length];
			record[0] = FormatMarker;
			record[1] = SchemaSetFormat;
			BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(2), setId);
			payload.CopyTo(record.AsSpan(2 + SchemaSetIdSize));
		}
		else if (_writeVersions is not null)
		{
			var used = _writeVersions.UsedSchemaVersions;
			Span<byte> header = stackalloc byte[2 + 5 + used.Count * 9];
			var pos = 0;
			header[pos++] = FormatMarker;
			header[pos++] = InlineVersionsFormat;
			WriteVarint(header, ref pos, (uint)used.Count);
			foreach (var (mapId, version) in used)
			{
				WriteVarint(header, ref pos, ZigZag(mapId));
				BinaryPrimitives.WriteSingleLittleEndian(header[pos..], version);
				pos += 4;
			}
			record = new byte[pos + payload.Length];
			header[..pos].CopyTo(record);
			payload.CopyTo(record.AsSpan(pos));
		}
		else
		{
			record = payload.ToArray();
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

	// Read at the versions the record names, the build's own mapping put back after; a failure names the record.
	// Caught by type, never by a filter: a filter in a generic method traps under WASM AOT (see SbxFrameExtensions).
	private T Deserialize(TKey key, ReadOnlySpan<byte> blob, bool salvaging)
	{
		T item = null!;
		Exception? failure = null;
		List<(int MapId, float Version)>? restore = null;
		try
		{
			_deserializer.Reset();
			var pos = 0;
			if (blob.Length >= 2 && blob[0] == FormatMarker && blob[1] == SchemaSetFormat)
			{
				var setId = BinaryPrimitives.ReadUInt32BigEndian(blob[2..]);
				pos = 2 + SchemaSetIdSize;
				var set = _sets.TryGetValue(setId, out var found) ? found : throw new InvalidOperationException(FormattableString.Invariant($"Schema set {setId:x8} is not stored here."));
				foreach (var (mapId, version) in set.Remaps)
				{
					Remap(mapId, version, ref restore);
				}
			}
			else if (blob.Length >= 2 && blob[0] == FormatMarker && blob[1] == InlineVersionsFormat)
			{
				pos = 2;
				var count = ReadVarint(blob, ref pos);
				for (var i = 0u; i < count; i++)
				{
					var mapId = ReadMapId(blob, ref pos);
					var version = BinaryPrimitives.ReadSingleLittleEndian(blob[pos..]);
					pos += 4;
					Remap(mapId, version, ref restore);
				}
			}
			else if (_setKeys is not null && !salvaging)
			{
				throw new InvalidOperationException("The record was written before schema sets: its versions are unknown, so the log is dropped and fetched again.");
			}
			item = _deserializer.Deserialize<T>(blob, ref pos);
		}
		catch (Exception ex)
		{
			failure = ex;
		}
		finally
		{
			if (restore is not null)
			{
				foreach (var (mapId, version) in restore)
				{
					_readVersions!.RemapSchemaVersion(mapId, version);
				}
			}
		}
		if (failure is not null)
		{
			throw new UnreadableRecordException(key.ToString() ?? "?", blob.Length, DescribeHeader(blob), failure);
		}
		return item;
	}

	// Reads a mapped id at the record's version until the record is read; an id this build does not map fails the record.
	void Remap(int mapId, float version, ref List<(int MapId, float Version)>? restore)
	{
		var current = _readVersions!.FindSchemaVersion(mapId) ?? throw new InvalidOperationException($"Map id {mapId} is not mapped by this build.");
		if (current != version)
		{
			(restore ??= []).Add((mapId, current));
			_readVersions.RemapSchemaVersion(mapId, version);
		}
	}

	// What the record says it was written with, or null for a record without a header.
	private string? DescribeHeader(ReadOnlySpan<byte> blob)
	{
		string? described = null;
		if (blob.Length >= 2 + SchemaSetIdSize && blob[0] == FormatMarker && blob[1] == SchemaSetFormat)
		{
			var setId = BinaryPrimitives.ReadUInt32BigEndian(blob[2..]);
			described = _sets.TryGetValue(setId, out var set)
				? FormattableString.Invariant($"schema set {setId:x8}: {string.Join(", ", set.Entries.Select(x => FormattableString.Invariant($"{x.MapId}@{x.Version}")))}")
				: FormattableString.Invariant($"schema set {setId:x8}, not stored here");
		}
		else if (blob.Length >= 2 && blob[0] == FormatMarker && blob[1] == InlineVersionsFormat)
		{
			var entries = new List<string>();
			try
			{
				var pos = 2;
				var count = ReadVarint(blob, ref pos);
				for (var i = 0u; i < count; i++)
				{
					var mapId = ReadMapId(blob, ref pos);
					entries.Add(FormattableString.Invariant($"{mapId}@{BinaryPrimitives.ReadSingleLittleEndian(blob[pos..])}"));
					pos += 4;
				}
				described = string.Join(", ", entries);
			}
			catch (Exception ex)
			{
				described = $"a broken header after [{string.Join(", ", entries)}] ({ex.GetType().Name}: {ex.Message})";
			}
		}
		return described;
	}

	private uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));

	private void WriteVarint(Span<byte> buffer, ref int pos, uint value)
	{
		while (value >= 0x80)
		{
			buffer[pos++] = (byte)(value | 0x80);
			value >>= 7;
		}
		buffer[pos++] = (byte)value;
	}

	private uint ReadVarint(ReadOnlySpan<byte> buffer, ref int pos)
	{
		uint value = 0;
		var shift = 0;
		byte b;
		do
		{
			b = buffer[pos++];
			value |= (uint)(b & 0x7F) << shift;
			shift += 7;
		}
		while ((b & 0x80) != 0);
		return value;
	}

	private int ReadMapId(ReadOnlySpan<byte> buffer, ref int pos)
	{
		var zigzag = ReadVarint(buffer, ref pos);
		return (int)(zigzag >> 1) ^ -(int)(zigzag & 1);
	}

	#endregion

	/// <summary>The item's record: what <see cref="GetAsync"/> and <see cref="GetAllAsync"/> read back. Register the schema set first.</summary>
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
