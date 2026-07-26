using System.Buffers;
using System.Net.WebSockets;

namespace Synqra;

/// <summary>
/// The first byte of every post-HELLO replication frame, disambiguating a domain-event payload from
/// the transport-control frames that drive client-driven subscriptions. Kept out of the SBX
/// (<see cref="INetworkSerializationService"/>) model layer on purpose: these are transport metadata,
/// not domain events, so they never touch the codegen-stamped model serializer.
/// </summary>
public enum ReplicationFrameTag : byte
{
	/// <summary>The remaining bytes are an SBX-serialized <see cref="TransportOperation"/> (a NewEvent1).</summary>
	Event = 0,

	/// <summary>Client → master: "subscribe me to this stream" — 16 bytes, a stream <see cref="Guid"/>.</summary>
	Subscribe = 1,

	/// <summary>Client → master: "unsubscribe me from this stream" — 16 bytes, a stream <see cref="Guid"/>.</summary>
	Unsubscribe = 2,

	/// <summary>Master → client: the authoritative snapshot of the streams this connection is now
	/// subscribed to — N*16 bytes, N stream <see cref="Guid"/>s (N may be zero). Sent right after HELLO
	/// and after every Subscribe/Unsubscribe so the client can detect an unexpected default.</summary>
	SubscriptionState = 3,
}

/// <summary>Send helpers for the tagged post-HELLO replication frames (see <see cref="ReplicationFrameTag"/>).</summary>
public static class ReplicationFramingExtensions
{
	/// <summary>Sends a single-stream control frame (<see cref="ReplicationFrameTag.Subscribe"/> /
	/// <see cref="ReplicationFrameTag.Unsubscribe"/>): 1 tag byte + the 16-byte stream id.</summary>
	public static async ValueTask SendStreamControlFrameAsync(this WebSocket socket, ReplicationFrameTag tag, Guid streamId, bool text, CancellationToken ct)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(17);
		try
		{
			buffer[0] = (byte)tag;
			streamId.TryWriteBytes(buffer.AsSpan(1, 16));
			await socket.SendAsync(new ArraySegment<byte>(buffer, 0, 17), text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, endOfMessage: true, ct);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	/// <summary>Sends the master → client subscription-state ack: 1 tag byte + N*16 bytes of stream ids.</summary>
	public static async ValueTask SendSubscriptionStateAsync(this WebSocket socket, IReadOnlyCollection<Guid> streams, bool text, CancellationToken ct)
	{
		var size = 1 + (streams.Count * 16);
		var buffer = ArrayPool<byte>.Shared.Rent(size);
		try
		{
			buffer[0] = (byte)ReplicationFrameTag.SubscriptionState;
			var offset = 1;
			foreach (var s in streams)
			{
				s.TryWriteBytes(buffer.AsSpan(offset, 16));
				offset += 16;
			}
			await socket.SendAsync(new ArraySegment<byte>(buffer, 0, size), text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, endOfMessage: true, ct);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}
}
