using System.Buffers.Binary;
using Synqra.BinarySerializer;

namespace Synqra;

public sealed class ReplicationRecordCodec
{
	private readonly ISbxSerializerFactory _serializerFactory;

	public ReplicationRecordCodec(ISbxSerializerFactory serializerFactory)
	{
		_serializerFactory = serializerFactory;
	}

	public byte[] SerializeEvent(Event ev) => Serialize(ev);
	public byte[] SerializeCommand(Command command) => Serialize(command);

	public Event DeserializeEvent(ReadOnlySpan<byte> data) => Deserialize<Event>(data);
	public Command DeserializeCommand(ReadOnlySpan<byte> data) => Deserialize<Command>(data);

	public uint Hash(ReadOnlySpan<byte> data)
	{
		var hash = 2166136261u;
		foreach (var value in data)
		{
			hash = unchecked((hash ^ value) * 16777619u);
		}
		return hash;
	}

	public byte[] EncodeConfirmedRecord(Event ev)
	{
		var payload = SerializeEvent(ev);
		var result = new byte[17 + payload.Length];
		result[0] = 1;
		ev.StreamId.TryWriteBytes(result.AsSpan(1, 16));
		payload.CopyTo(result.AsSpan(17));
		return result;
	}

	public Event DecodeConfirmedRecord(ReadOnlySpan<byte> data)
	{
		if (data.Length < 18 || data[0] != 1)
		{
			throw new InvalidDataException("Unsupported confirmed-event record format.");
		}

		var streamId = new Guid(data.Slice(1, 16));
		var ev = DeserializeEvent(data[17..]);
		ev.StreamId = streamId;
		return ev;
	}

	public uint HashConfirmedRecord(ReadOnlySpan<byte> data)
	{
		if (data.Length < 18 || data[0] != 1)
		{
			throw new InvalidDataException("Unsupported confirmed-event record format.");
		}
		return Hash(data[17..]);
	}

	public byte[] EncodePendingBatch(PendingCommandBatch batch)
	{
		var commandData = SerializeCommand(batch.Command);
		var eventData = batch.Events.Select(SerializeEvent).ToArray();
		var length = 1 + 16 + 16 + 4 + commandData.Length + 4
			+ eventData.Sum(x => 4 + x.Length);
		var result = new byte[length];
		var offset = 0;
		result[offset++] = 1;
		batch.Command.StreamId.TryWriteBytes(result.AsSpan(offset, 16));
		offset += 16;
		(batch.Options?.ExpectedLastEventId ?? Guid.Empty).TryWriteBytes(result.AsSpan(offset, 16));
		offset += 16;
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), commandData.Length);
		offset += 4;
		commandData.CopyTo(result.AsSpan(offset));
		offset += commandData.Length;
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), eventData.Length);
		offset += 4;
		foreach (var data in eventData)
		{
			BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), data.Length);
			offset += 4;
			data.CopyTo(result.AsSpan(offset));
			offset += data.Length;
		}
		return result;
	}

	public PendingCommandBatch DecodePendingBatch(ReadOnlySpan<byte> data)
	{
		if (data.Length < 41 || data[0] != 1)
		{
			throw new InvalidDataException("Unsupported pending-command record format.");
		}

		var offset = 1;
		var streamId = new Guid(data.Slice(offset, 16));
		offset += 16;
		var expectedLastEventId = new Guid(data.Slice(offset, 16));
		offset += 16;
		var commandLength = ReadLength(data, ref offset);
		var command = DeserializeCommand(data.Slice(offset, commandLength));
		offset += commandLength;
		command.StreamId = streamId;
		var eventCount = ReadLength(data, ref offset);
		if (eventCount > (data.Length - offset) / 4)
		{
			throw new InvalidDataException("Pending-command record contains an invalid event count.");
		}
		var events = new List<Event>(eventCount);
		for (var i = 0; i < eventCount; i++)
		{
			var eventLength = ReadLength(data, ref offset);
			var ev = DeserializeEvent(data.Slice(offset, eventLength));
			offset += eventLength;
			ev.StreamId = streamId;
			events.Add(ev);
		}

		if (offset != data.Length)
		{
			throw new InvalidDataException("Pending-command record contains trailing data.");
		}

		return new PendingCommandBatch
		{
			Command = command,
			Events = events,
			Options = expectedLastEventId == Guid.Empty
				? null
				: new CommandSubmissionOptions { ExpectedLastEventId = expectedLastEventId },
		};
	}

	private byte[] Serialize<T>(T value)
	{
		var serializer = _serializerFactory.CreateSerializer();
		var buffer = new byte[16 * 1024];
		while (true)
		{
			try
			{
				var offset = 0;
				serializer.Reset();
				serializer.Serialize(buffer, value, ref offset);
				return buffer.AsSpan(0, offset).ToArray();
			}
			catch (Exception ex) when (IsCapacityException(ex))
			{
				buffer = new byte[buffer.Length * 2];
			}
		}
	}

	private T Deserialize<T>(ReadOnlySpan<byte> data)
	{
		var serializer = _serializerFactory.CreateSerializer();
		var offset = 0;
		return serializer.Deserialize<T>(data, ref offset);
	}

	private static int ReadLength(ReadOnlySpan<byte> data, ref int offset)
	{
		if (offset > data.Length - 4)
		{
			throw new InvalidDataException("Replication record ended before its next length prefix.");
		}

		var length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
		offset += 4;
		if (length < 0 || length > data.Length - offset)
		{
			throw new InvalidDataException("Replication record contains an invalid length prefix.");
		}
		return length;
	}

	private static bool IsCapacityException(Exception ex)
	{
		return false
			|| ex is IndexOutOfRangeException
			|| ex is ArgumentOutOfRangeException
			|| ex is OverflowException;
	}
}
