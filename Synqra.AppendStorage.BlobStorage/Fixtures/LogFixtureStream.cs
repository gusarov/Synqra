using System.Runtime.CompilerServices;
using System.Text.Json;
using Synqra.BinarySerializer;
using Synqra.BlobStorage;

namespace Synqra.AppendStorage.BlobStorage.Fixtures;

/// <summary>
/// A committed, append-only stream of local-log captures: each file holds the exact records (schema set included) one build wrote
/// for one model version, as a browser's IndexedDB log holds them. Every later build must read every capture; a capture is never
/// written again once it exists, so the stream only grows. A model version without a capture is a gap the host's CI reports.
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

	/// <summary>One record per line: its key and its bytes, as the log stored them.</summary>
	sealed class FixtureBlobStorage : IBlobStorage<(Guid Stream, Guid Sequence)>
	{
		readonly SortedDictionary<(Guid Stream, Guid Sequence), byte[]> _blobs = new(Comparer<(Guid Stream, Guid Sequence)>.Create((a, b) => a.Stream.CompareBigEndian(b.Stream) is var first && first != 0 ? first : a.Sequence.CompareBigEndian(b.Sequence)));

		public IEnumerable<string> Describe() => _blobs.Select(x => JsonSerializer.Serialize(new FixtureRecord(x.Key.Stream.ToString("N"), x.Key.Sequence.ToString("N"), Convert.ToBase64String(x.Value))));

		public void Load(IEnumerable<string> lines)
		{
			foreach (var line in lines.Where(x => !string.IsNullOrWhiteSpace(x)))
			{
				var record = JsonSerializer.Deserialize<FixtureRecord>(line) ?? throw new InvalidDataException($"Not a fixture record: {line}");
				_blobs[(Guid.ParseExact(record.Stream, "N"), Guid.ParseExact(record.Sequence, "N"))] = Convert.FromBase64String(record.Blob);
			}
		}

		public async IAsyncEnumerable<(Guid Stream, Guid Sequence)> EnumerateKeysAsync((Guid Stream, Guid Sequence) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			foreach (var key in _blobs.Keys.Where(k => from == default || _blobs.Comparer.Compare(k, from) > 0).ToList())
			{
				await Task.Yield();
				yield return key;
			}
		}

		public ValueTask<byte[]> ReadBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default) => ValueTask.FromResult(_blobs[key]);

		public ValueTask WriteBlobAsync((Guid Stream, Guid Sequence) key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
		{
			_blobs[key] = blob.ToArray();
			return ValueTask.CompletedTask;
		}

		public ValueTask DeleteBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default)
		{
			_blobs.Remove(key);
			return ValueTask.CompletedTask;
		}

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	sealed record FixtureRecord(string Stream, string Sequence, string Blob);
}
