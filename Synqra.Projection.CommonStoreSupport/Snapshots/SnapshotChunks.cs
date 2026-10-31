using Synqra.BinarySerializer;

namespace Synqra;

/// <summary>
/// Writes snapshot records into chunks (plans/snapshot.md §3, format 1): each chunk is its own SBX context, so it decodes alone,
/// and closes at the first record boundary past the budget. A record is never split; one larger than the budget goes alone in a
/// chunk sized for it.
/// </summary>
public sealed class SnapshotChunkWriter
{
	readonly ISbxSerializer _serializer;
	readonly Guid _snapshotId;
	readonly int _budget;
	byte[] _buffer;
	int _pos;
	long _count;

	public SnapshotChunkWriter(ISbxSerializer serializer, Guid snapshotId, int budget = 56 * 1024)
	{
		_serializer = serializer;
		_snapshotId = snapshotId;
		_budget = budget;
		_buffer = new byte[budget + 8 * 1024];
	}

	/// <summary>Chunks completed so far.</summary>
	public long ChunkCount { get; private set; }

	/// <summary>Records written so far, in completed chunks and the open one.</summary>
	public long RecordCount { get; private set; }

	/// <summary>Writes a record; returns the chunk it completed, if it completed one.</summary>
	public SnapshotChunk? Write(SnapshotRecord record)
	{
		SnapshotChunk? completed = null;
		var written = false;
		while (!written)
		{
			if (_pos == 0)
			{
				_serializer.Reset(); // a chunk starts a context of its own
			}
			var pos = _pos;
			written = TryWrite(record, ref pos);
			if (written)
			{
				_pos = pos;
				_count++;
				RecordCount++;
			}
			else if (_count > 0)
			{
				completed = CloseChunk(); // the record goes first in the next chunk
			}
			else
			{
				_buffer = new byte[_buffer.Length * 2]; // a record larger than the chunk budget, alone
			}
		}
		if (_pos >= _budget)
		{
			completed = CloseChunk();
		}
		return completed;
	}

	/// <summary>The open chunk, if it holds a record.</summary>
	public SnapshotChunk? Complete() => _count > 0 ? CloseChunk() : null;

	bool TryWrite(SnapshotRecord record, ref int pos)
	{
		var written = false;
		try
		{
			var span = _buffer.AsSpan();
			_serializer.Serialize(span, ref pos, record.ComponentId);
			_serializer.Serialize(span, ref pos, record.Owner);
			_serializer.Serialize(span, ref pos, record.Version);
			_serializer.Serialize<object>(span, ref pos, record.Data);
			written = true;
		}
		// By type, never through an exception filter: WASM AOT traps in a filter of a generic serializer path (#190).
		catch (SbxBufferTooSmallException)
		{
		}
		catch (IndexOutOfRangeException)
		{
		}
		catch (ArgumentOutOfRangeException)
		{
		}
		return written;
	}

	SnapshotChunk CloseChunk()
	{
		var chunk = new SnapshotChunk
		{
			SnapshotId = _snapshotId,
			Index = ChunkCount,
			Count = _count,
			Payload = _buffer.AsSpan(0, _pos).ToArray(),
		};
		ChunkCount++;
		_pos = 0;
		_count = 0;
		return chunk;
	}
}

/// <summary>Reads the records of a snapshot's chunks with the reader the header built (<see cref="SnapshotSchema.CreateReader"/>).</summary>
public sealed class SnapshotChunkReader(ISbxSerializer serializer)
{
	public List<SnapshotRecord> Read(SnapshotChunk chunk)
	{
		serializer.Reset();
		var records = new List<SnapshotRecord>((int)chunk.Count);
		ReadOnlySpan<byte> payload = chunk.Payload;
		var pos = 0;
		for (var i = 0L; i < chunk.Count; i++)
		{
			var componentId = serializer.DeserializeGuid(payload, ref pos);
			var owner = serializer.DeserializeNullableGuid(payload, ref pos);
			var version = serializer.DeserializeNullableGuid(payload, ref pos);
			var data = serializer.Deserialize<object>(payload, ref pos)
				?? throw new FormatException($"Snapshot {chunk.SnapshotId} chunk {chunk.Index}: record {i} ({componentId}) has no data.");
			records.Add(new SnapshotRecord { ComponentId = componentId, Owner = owner, Version = version, Data = data, });
		}
		if (pos != payload.Length)
		{
			throw new FormatException($"Snapshot {chunk.SnapshotId} chunk {chunk.Index}: {chunk.Count} records end at {pos} of {payload.Length} bytes.");
		}
		return records;
	}
}
