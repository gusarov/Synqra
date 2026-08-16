using System.Buffers;
using System.Net.WebSockets;

namespace Synqra;

/// <summary>
/// Sends one <see cref="TransportOperation"/> over a replication socket. Every post-HELLO frame —
/// domain events (<see cref="EventEnvelope"/>) and subscription control (<see cref="SubscribeRequest"/>,
/// <see cref="UnsubscribeRequest"/>, <see cref="SubscriptionState"/>) alike — is just an SBX-serialized
/// <see cref="TransportOperation"/>. The polymorphic model layer already discriminates them, so there
/// is no hand-rolled tag byte and no framing layer to keep in sync at both ends: adding a new control
/// message is a new subclass, not a new wire constant plus a parser on each side.
/// </summary>
public static class ReplicationTransportExtensions
{
	/// <summary>
	/// Serializes <paramref name="operation"/> into the caller's reusable <paramref name="buffer"/> and
	/// sends it. Use this inside replay/broadcast loops so one rented buffer serves every message.
	/// </summary>
	public static async ValueTask SendOperationAsync(this WebSocket socket, INetworkSerializationService serializer, TransportOperation operation, byte[] buffer, CancellationToken ct)
	{
		var payload = serializer.Serialize<TransportOperation>(operation, new ArraySegment<byte>(buffer));
		await socket.SendAsync(payload, serializer.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, endOfMessage: true, ct);
	}

	/// <summary>Rents a frame buffer for a single one-off send (control messages, not replay loops).</summary>
	public static async ValueTask SendOperationAsync(this WebSocket socket, INetworkSerializationService serializer, TransportOperation operation, CancellationToken ct)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(EventReplicationService.DefaultFrameSize);
		try
		{
			await socket.SendOperationAsync(serializer, operation, buffer, ct);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}
}
