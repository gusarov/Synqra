using System.Buffers.Binary;
using System.Text;

namespace Synqra;

public enum ReplicationFrameKind : byte
{
	Hello = 1,
	HelloAccepted = 2,
	ConfirmedEvent = 3,
	DeleteConfirmedEvent = 4,
	RepairComplete = 5,
	SubmitCommand = 6,
	CommandAcknowledged = 7,
	CommandRejected = 8,
}

public sealed class ReplicationHello
{
	public required ulong Magic { get; init; }
	public required IReadOnlyDictionary<Guid, uint> ConfirmedEvents { get; init; }
}

public sealed class SubmittedCommand
{
	public required Command Command { get; init; }
	public CommandSubmissionOptions? Options { get; init; }
}

public sealed class RejectedCommand
{
	public required Guid CommandId { get; init; }
	public required string Message { get; init; }
}

public sealed class ReplicationProtocol
{
	private readonly ReplicationRecordCodec _codec;

	public ReplicationProtocol(ReplicationRecordCodec codec)
	{
		_codec = codec;
	}

	public byte[] CreateHello(ulong magic, IReadOnlyCollection<ConfirmedEventDigest> confirmedEvents)
	{
		var result = new byte[14 + confirmedEvents.Count * 20];
		result[0] = (byte)ReplicationFrameKind.Hello;
		result[1] = 1;
		BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(2, 8), magic);
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(10, 4), confirmedEvents.Count);
		var offset = 14;
		foreach (var item in confirmedEvents)
		{
			item.EventId.TryWriteBytes(result.AsSpan(offset, 16));
			offset += 16;
			BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), item.Hash);
			offset += 4;
		}
		return result;
	}

	public ReplicationHello ReadHello(ReadOnlySpan<byte> frame)
	{
		ValidateHeader(frame, ReplicationFrameKind.Hello, minimumLength: 14);
		if (frame[1] != 1)
		{
			throw new InvalidDataException($"Unsupported replication protocol version {frame[1]}.");
		}

		var count = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(10, 4));
		if (count < 0
			|| count > (frame.Length - 14) / 20
			|| frame.Length != 14 + count * 20
		)
		{
			throw new InvalidDataException("Replication hello contains an invalid digest count.");
		}

		var confirmedEvents = new Dictionary<Guid, uint>(count);
		var offset = 14;
		for (var i = 0; i < count; i++)
		{
			var eventId = new Guid(frame.Slice(offset, 16));
			offset += 16;
			var hash = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset, 4));
			offset += 4;
			if (!confirmedEvents.TryAdd(eventId, hash))
			{
				throw new InvalidDataException($"Replication hello contains duplicate event id {eventId}.");
			}
		}

		return new ReplicationHello
		{
			Magic = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(2, 8)),
			ConfirmedEvents = confirmedEvents,
		};
	}

	public byte[] CreateHelloAccepted(ulong magic)
	{
		var result = new byte[10];
		result[0] = (byte)ReplicationFrameKind.HelloAccepted;
		result[1] = 1;
		BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(2, 8), magic);
		return result;
	}

	public void ValidateHelloAccepted(ReadOnlySpan<byte> frame, ulong expectedMagic)
	{
		ValidateHeader(frame, ReplicationFrameKind.HelloAccepted, exactLength: 10);
		if (frame[1] != 1)
		{
			throw new InvalidDataException($"Unsupported replication protocol version {frame[1]}.");
		}
		var magic = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(2, 8));
		if (magic != expectedMagic)
		{
			throw new InvalidDataException($"Replication magic {magic:X16} does not match {expectedMagic:X16}.");
		}
	}

	public byte[] CreateConfirmedEvent(Event ev)
	{
		var payload = _codec.SerializeEvent(ev);
		return CreatePayloadFrame(ReplicationFrameKind.ConfirmedEvent, payload);
	}

	public uint HashEvent(Event ev) => _codec.Hash(_codec.SerializeEvent(ev));

	public Event ReadConfirmedEvent(ReadOnlySpan<byte> frame)
	{
		ValidateHeader(frame, ReplicationFrameKind.ConfirmedEvent, minimumLength: 2);
		return _codec.DeserializeEvent(frame[1..]);
	}

	public byte[] CreateDeleteConfirmedEvent(Guid eventId)
	{
		var result = new byte[17];
		result[0] = (byte)ReplicationFrameKind.DeleteConfirmedEvent;
		eventId.TryWriteBytes(result.AsSpan(1, 16));
		return result;
	}

	public Guid ReadEventId(ReadOnlySpan<byte> frame, ReplicationFrameKind expectedKind)
	{
		ValidateHeader(frame, expectedKind, exactLength: 17);
		return new Guid(frame.Slice(1, 16));
	}

	public byte[] CreateRepairComplete() => [(byte)ReplicationFrameKind.RepairComplete];

	public byte[] CreateSubmitCommand(PendingCommandBatch batch)
	{
		var payload = _codec.SerializeCommand(batch.Command);
		var result = new byte[21 + batch.Events.Count * 16 + payload.Length];
		result[0] = (byte)ReplicationFrameKind.SubmitCommand;
		(batch.Options?.ExpectedLastEventId ?? Guid.Empty).TryWriteBytes(result.AsSpan(1, 16));
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(17, 4), batch.Events.Count);
		var offset = 21;
		foreach (var ev in batch.Events)
		{
			ev.EventId.TryWriteBytes(result.AsSpan(offset, 16));
			offset += 16;
		}
		payload.CopyTo(result.AsSpan(offset));
		return result;
	}

	public SubmittedCommand ReadSubmittedCommand(ReadOnlySpan<byte> frame)
	{
		ValidateHeader(frame, ReplicationFrameKind.SubmitCommand, minimumLength: 22);
		var expectedLastEventId = new Guid(frame.Slice(1, 16));
		var eventCount = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(17, 4));
		if (eventCount < 0
			|| eventCount > (frame.Length - 22) / 16
		)
		{
			throw new InvalidDataException("Submitted command contains an invalid event-id count.");
		}
		var allocatedEventIds = new Guid[eventCount];
		var offset = 21;
		for (var i = 0; i < eventCount; i++)
		{
			allocatedEventIds[i] = new Guid(frame.Slice(offset, 16));
			offset += 16;
		}
		return new SubmittedCommand
		{
			Command = _codec.DeserializeCommand(frame[offset..]),
			Options = new CommandSubmissionOptions
			{
				ExpectedLastEventId = expectedLastEventId,
				AllocatedEventIds = allocatedEventIds,
			},
		};
	}

	public byte[] CreateCommandAcknowledged(Guid commandId)
	{
		var result = new byte[17];
		result[0] = (byte)ReplicationFrameKind.CommandAcknowledged;
		commandId.TryWriteBytes(result.AsSpan(1, 16));
		return result;
	}

	public byte[] CreateCommandRejected(Guid commandId, string message)
	{
		var payload = Encoding.UTF8.GetBytes(message);
		var result = new byte[17 + payload.Length];
		result[0] = (byte)ReplicationFrameKind.CommandRejected;
		commandId.TryWriteBytes(result.AsSpan(1, 16));
		payload.CopyTo(result.AsSpan(17));
		return result;
	}

	public RejectedCommand ReadCommandRejected(ReadOnlySpan<byte> frame)
	{
		ValidateHeader(frame, ReplicationFrameKind.CommandRejected, minimumLength: 17);
		return new RejectedCommand
		{
			CommandId = new Guid(frame.Slice(1, 16)),
			Message = Encoding.UTF8.GetString(frame[17..]),
		};
	}

	public ReplicationFrameKind ReadKind(ReadOnlySpan<byte> frame)
	{
		if (frame.IsEmpty)
		{
			throw new InvalidDataException("Replication frame has an unknown or missing kind.");
		}
		return frame[0] switch
		{
			(byte)ReplicationFrameKind.Hello => ReplicationFrameKind.Hello,
			(byte)ReplicationFrameKind.HelloAccepted => ReplicationFrameKind.HelloAccepted,
			(byte)ReplicationFrameKind.ConfirmedEvent => ReplicationFrameKind.ConfirmedEvent,
			(byte)ReplicationFrameKind.DeleteConfirmedEvent => ReplicationFrameKind.DeleteConfirmedEvent,
			(byte)ReplicationFrameKind.RepairComplete => ReplicationFrameKind.RepairComplete,
			(byte)ReplicationFrameKind.SubmitCommand => ReplicationFrameKind.SubmitCommand,
			(byte)ReplicationFrameKind.CommandAcknowledged => ReplicationFrameKind.CommandAcknowledged,
			(byte)ReplicationFrameKind.CommandRejected => ReplicationFrameKind.CommandRejected,
			_ => throw new InvalidDataException("Replication frame has an unknown or missing kind."),
		};
	}

	private static byte[] CreatePayloadFrame(ReplicationFrameKind kind, ReadOnlySpan<byte> payload)
	{
		var result = new byte[1 + payload.Length];
		result[0] = (byte)kind;
		payload.CopyTo(result.AsSpan(1));
		return result;
	}

	private static void ValidateHeader(
		  ReadOnlySpan<byte> frame
		, ReplicationFrameKind expectedKind
		, int? exactLength = null
		, int? minimumLength = null
	)
	{
		if (frame.IsEmpty || frame[0] != (byte)expectedKind)
		{
			throw new InvalidDataException($"Expected {expectedKind} replication frame.");
		}
		if (exactLength is int exact && frame.Length != exact)
		{
			throw new InvalidDataException($"{expectedKind} frame has length {frame.Length}; expected {exact}.");
		}
		if (minimumLength is int minimum && frame.Length < minimum)
		{
			throw new InvalidDataException($"{expectedKind} frame has length {frame.Length}; expected at least {minimum}.");
		}
	}
}
