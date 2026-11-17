using System.Buffers.Binary;

namespace Synqra.BinarySerializer;

/// <summary>
/// The schemas bundle: a serializer's entire mapping, every bindable map id at the schema version it writes and reads, as a
/// snapshot header carries it. An SBX payload names no versions, so a stored record or a connection names its bundle instead.
/// Its id is the hash of its canonical content: every writer with the same mapping names the same bundle, without coordination.
/// </summary>
public sealed class SchemasBundle
{
	/// <summary>The bundle of <paramref name="versions"/>' mapping as it stands.</summary>
	public SchemasBundle(ISbxSchemaVersions versions)
	{
		Entries = versions.MappedSchemaVersions.Select(x => (MapId: x.Key, Version: x.Value)).OrderBy(x => x.MapId).ToArray();
		Content = DescribeEntries(Entries);
		Id = HashContent(Content);
	}

	/// <summary>A stored bundle: its content must hash to <paramref name="id"/>, or it is refused.</summary>
	public SchemasBundle(uint id, byte[] content)
	{
		Content = content;
		Id = HashContent(content);
		if (Id != id)
		{
			throw new InvalidDataException(FormattableString.Invariant($"Schemas bundle {id:x8} does not match its content ({Id:x8})."));
		}
		Entries = ReadEntries(content);
	}

	public uint Id { get; }

	/// <summary>The entry count, then per map id (ascending) the id as a zigzag varint and its version as a little-endian float.</summary>
	public byte[] Content { get; }

	public IReadOnlyList<(int MapId, float Version)> Entries { get; }

	/// <summary>Reads and writes every map id <paramref name="reader"/> maps at this bundle's version of it; an id it does not map is left alone.</summary>
	public void Apply(ISbxSchemaVersions reader)
	{
		foreach (var (mapId, version) in Entries)
		{
			if (reader.FindSchemaVersion(mapId) is { } current && current != version)
			{
				reader.RemapSchemaVersion(mapId, version);
			}
		}
	}

	public override string ToString() => FormattableString.Invariant($"schemas bundle {Id:x8}");

	byte[] DescribeEntries(IReadOnlyList<(int MapId, float Version)> entries)
	{
		var buffer = new byte[5 + entries.Count * 9];
		var pos = 0;
		WriteVarint(buffer, ref pos, (uint)entries.Count);
		foreach (var (mapId, version) in entries)
		{
			WriteVarint(buffer, ref pos, (uint)((mapId << 1) ^ (mapId >> 31)));
			BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(pos), version);
			pos += 4;
		}
		return buffer.AsSpan(0, pos).ToArray();
	}

	(int MapId, float Version)[] ReadEntries(byte[] content)
	{
		var pos = 0;
		var count = ReadVarint(content, ref pos);
		var entries = new (int MapId, float Version)[count];
		for (var i = 0; i < count; i++)
		{
			var zigzag = ReadVarint(content, ref pos);
			entries[i] = ((int)(zigzag >> 1) ^ -(int)(zigzag & 1), BinaryPrimitives.ReadSingleLittleEndian(content.AsSpan(pos)));
			pos += 4;
		}
		return entries;
	}

	// FNV-1a, 32 bits.
	uint HashContent(ReadOnlySpan<byte> content)
	{
		var hash = 2166136261u;
		foreach (var b in content)
		{
			hash = (hash ^ b) * 16777619u;
		}
		return hash;
	}

	void WriteVarint(Span<byte> buffer, ref int pos, uint value)
	{
		while (value >= 0x80)
		{
			buffer[pos++] = (byte)(value | 0x80);
			value >>= 7;
		}
		buffer[pos++] = (byte)value;
	}

	uint ReadVarint(ReadOnlySpan<byte> buffer, ref int pos)
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
}
