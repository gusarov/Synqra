using System.Runtime.CompilerServices;
using System.Text.Json;
using Synqra.BinarySerializer;
using Synqra.BlobStorage;

namespace Synqra.AppendStorage.BlobStorage.Fixtures;

/// <summary>
/// A committed, append-only stream of local-log captures: each file holds the exact records one build wrote for one model
/// version, as a browser's IndexedDB log holds them (pure SBX bytes, each naming its schemas bundle, and the bundle itself).
/// Every later build must read every capture; a capture is never written again once it exists, so the stream only grows. A model
/// version without a capture is a gap the host's CI reports.
/// </summary>
public sealed class LogFixtureStream(string folder)
{
	public const string Extension = ".sbxlog.jsonl";

	// The stream a capture's events are filed under when they name none.
	readonly Guid _fixtureStream = new Guid("5c4e3a00-0000-7000-8000-000000000001");

	public string Folder => folder;

	/// <summary>Whether the stream holds a capture of <paramref name="name"/>.</summary>
	public bool Holds(string name) => File.Exists(Path.Combine(folder, name + Extension));

	/// <summary>The captures in the stream, by name.</summary>
	public IEnumerable<string> Names => Directory.Exists(folder)
		? Directory.EnumerateFiles(folder, "*" + Extension).Select(x => Path.GetFileName(x)[..^Extension.Length]).Order(StringComparer.Ordinal)
		: [];

	/// <summary>
	/// Writes <paramref name="events"/> as <paramref name="writer"/>'s build stores them, unless the stream holds the name already
	/// (a capture is never written again). True when it wrote one.
	/// </summary>
	public async Task<bool> RecordAsync(string name, ISbxSerializerFactory writer, IEnumerable<Event> events, CancellationToken cancellationToken = default)
	{
		var recorded = false;
		if (!Holds(name))
		{
			var blobs = new FixtureBlobStorage();
			var storage = new BlobEventStorage(blobs, writer);
			foreach (var ev in events)
			{
				ev.StreamId = ev.StreamId == default ? _fixtureStream : ev.StreamId;
				ev.SequenceId = ev.SequenceId == default ? Guid.CreateVersion7() : ev.SequenceId; // a log keys its records by (stream, sequence)
				await storage.AppendAsync(ev, cancellationToken);
			}
			Directory.CreateDirectory(folder);
			await File.WriteAllLinesAsync(Path.Combine(folder, name + Extension), blobs.Describe(), cancellationToken);
			recorded = true;
		}
		return recorded;
	}

	/// <summary>Every event of the capture, read by <paramref name="reader"/>'s build as a browser's log reads its records.</summary>
	public async IAsyncEnumerable<Event> ReadAsync(string name, ISbxSerializerFactory reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var blobs = new FixtureBlobStorage();
		blobs.Load(await File.ReadAllLinesAsync(Path.Combine(folder, name + Extension), cancellationToken));
		await foreach (var ev in new BlobEventStorage(blobs, reader).GetAllAsync(default, cancellationToken))
		{
			yield return ev;
		}
	}

	/// <summary>One line per side entry (the schemas bundles), then one per record (its key, its tag and its bytes).</summary>
	sealed class FixtureBlobStorage : IBlobStorage<(Guid Stream, Guid Sequence)>, ITaggedBlobStorage<(Guid Stream, Guid Sequence)>
	{
		readonly SortedDictionary<(Guid Stream, Guid Sequence), TaggedBlob> _records = new(Comparer<(Guid Stream, Guid Sequence)>.Create((a, b) => a.Stream.CompareBigEndian(b.Stream) is var first && first != 0 ? first : a.Sequence.CompareBigEndian(b.Sequence)));
		readonly SortedDictionary<string, byte[]> _sides = new(StringComparer.Ordinal); // "{side}/{key}"

		public IEnumerable<string> Describe()
			=> _sides.Select(x => JsonSerializer.Serialize(new FixtureLine(null, null, null, x.Key, Convert.ToBase64String(x.Value))))
				.Concat(_records.Select(x => JsonSerializer.Serialize(new FixtureLine(x.Key.Stream.ToString("N"), x.Key.Sequence.ToString("N"), x.Value.Tag, null, Convert.ToBase64String(x.Value.Blob)))));

		public void Load(IEnumerable<string> lines)
		{
			foreach (var line in lines.Where(x => !string.IsNullOrWhiteSpace(x)))
			{
				var record = JsonSerializer.Deserialize<FixtureLine>(line) ?? throw new InvalidDataException($"Not a fixture line: {line}");
				if (record.Stream is null)
				{
					_sides[record.Side ?? throw new InvalidDataException($"A side entry without its name: {line}")] = Convert.FromBase64String(record.Bytes);
				}
				else
				{
					_records[(Guid.ParseExact(record.Stream, "N"), Guid.ParseExact(record.Sequence!, "N"))] = new TaggedBlob(Convert.FromBase64String(record.Bytes), record.Tag);
				}
			}
		}

		public async IAsyncEnumerable<(Guid Stream, Guid Sequence)> EnumerateKeysAsync((Guid Stream, Guid Sequence) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await foreach (var (key, _) in EnumerateTaggedBlobsAsync(from, cancellationToken))
			{
				yield return key;
			}
		}

		public async IAsyncEnumerable<((Guid Stream, Guid Sequence) Key, TaggedBlob Blob)> EnumerateTaggedBlobsAsync((Guid Stream, Guid Sequence) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			foreach (var record in _records.Where(x => from == default || _records.Comparer.Compare(x.Key, from) > 0).ToList())
			{
				await Task.Yield();
				yield return (record.Key, record.Value);
			}
		}

		public ValueTask<byte[]> ReadBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default) => ValueTask.FromResult(_records[key].Blob);

		public ValueTask<TaggedBlob> ReadTaggedBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default) => ValueTask.FromResult(_records[key]);

		public ValueTask WriteBlobAsync((Guid Stream, Guid Sequence) key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
		{
			_records[key] = new TaggedBlob(blob.ToArray(), null);
			return ValueTask.CompletedTask;
		}

		public ValueTask WriteBlobAsync((Guid Stream, Guid Sequence) key, ReadOnlyMemory<byte> blob, uint tag, string? json, CancellationToken cancellationToken = default)
		{
			_records[key] = new TaggedBlob(blob.ToArray(), tag);
			return ValueTask.CompletedTask;
		}

		public ValueTask<IReadOnlyDictionary<string, byte[]>> ReadSideEntriesAsync(string side, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyDictionary<string, byte[]>>(_sides.Where(x => x.Key.StartsWith(side + "/", StringComparison.Ordinal)).ToDictionary(x => x.Key[(side.Length + 1)..], x => x.Value));

		public ValueTask StoreSideEntryAsync(string side, string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
		{
			_sides.TryAdd(side + "/" + key, content.ToArray());
			return ValueTask.CompletedTask;
		}

		public ValueTask DeleteBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default)
		{
			_records.Remove(key);
			return ValueTask.CompletedTask;
		}

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	/// <summary>A side entry line names its side and key ("{side}/{key}"); a record line has its key and tag (none when written without one).</summary>
	sealed record FixtureLine(string? Stream, string? Sequence, uint? Tag, string? Side, string Bytes);
}
