using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;

namespace Synqra.Replication.AspNetCore;

/// <summary>
/// Server (master) side of Synqra's event replication protocol — the counterpart to the
/// client-side <see cref="EventReplicationService"/>. Extracted from the test-only
/// simulator (Synqra.Tests/Simulator/SynqraTestNode.cs's master-host branch) into a
/// reusable production endpoint: every event a connected client sends gets applied to
/// this process's own <see cref="IProjection"/>, durably appended via this process's own
/// <see cref="IAppendStorage{Event, Guid}"/> (so the server's own backing store — e.g.
/// Mongo — is the thing that actually persists), and broadcast to every other connected
/// client so they converge too.
/// </summary>
public static class SynqraReplicationEndpointExtensions
{
	/// <summary>
	/// Wires the WebSocket replication endpoint at <paramref name="path"/> (default
	/// matches <see cref="EventReplicationService"/>'s hardcoded client URL,
	/// "/api/synqra/ws"). Resolves <see cref="INetworkSerializationService"/>,
	/// <see cref="IProjection"/>, and <see cref="IAppendStorage{Event, Guid}"/> from DI —
	/// register those (e.g. AddSbxSerializer + AddMongoDbSynqraStore +
	/// AddAppendStorageMongoDb&lt;Event, Guid&gt;) before calling this.
	/// <para>
	/// Pass <paramref name="serviceKey"/> when the process hosts more than one independent
	/// Synqra-backed feature — the endpoint will resolve <see cref="IProjection"/> and
	/// <see cref="IAppendStorage{Event, Guid}"/> from the keyed DI slot so each feature
	/// operates against its own store rather than sharing one global singleton.
	/// </para>
	/// </summary>
	public static WebApplication MapSynqraReplicationEndpoint(this WebApplication app, string path = "/api/synqra/ws", string? serviceKey = null)
	{
		app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

		var sockets = new ConcurrentBag<WebSocket>();
		var broadcastGate = new SemaphoreSlim(1, 1);
		var knownEvents = new ConcurrentDictionary<Guid, object?>();

		app.Map(path, async ctx =>
		{
			var networkSerializationService = ctx.RequestServices.GetRequiredService<INetworkSerializationService>();
			if (!ctx.WebSockets.IsWebSocketRequest)
			{
				ctx.Response.StatusCode = 400;
				return;
			}
			using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

			#region HELLO — from client
			var helloBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
			if (helloBytes is null)
			{
				return;
			}
			if (helloBytes.Length != 8)
			{
				throw new InvalidOperationException($"Protocol negotiation failed: received {helloBytes.Length} bytes instead of 8.");
			}
			var magic = BitConverter.ToUInt64(helloBytes);
			if (magic != networkSerializationService.Magic)
			{
				throw new InvalidOperationException($"Protocol negotiation failed: received magic {magic:X16} instead of {networkSerializationService.Magic:X16}.");
			}
			#endregion

			#region HELLO — to client
			// Register for broadcasts BEFORE answering HELLO, and do both under the same
			// gate as every broadcast SendAsync on this socket: the moment the client
			// observes this reply, every later broadcast is guaranteed to reach it, and
			// this reply can never interleave with a concurrent broadcast write.
			var magicBytes = BitConverter.GetBytes(networkSerializationService.Magic);
			await broadcastGate.WaitAsync(ctx.RequestAborted);
			try
			{
				sockets.Add(socket);
				await socket.SendAsync(magicBytes, WebSocketMessageType.Binary, endOfMessage: true, ctx.RequestAborted);
			}
			finally
			{
				broadcastGate.Release();
			}
			#endregion

			while (!ctx.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
			{
				var messageBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
				if (messageBytes is null)
				{
					break;
				}
				var operation = networkSerializationService.Deserialize<TransportOperation>(messageBytes);
				if (operation is not NewEvent1 newEvent1)
				{
					throw new NotSupportedException($"Unsupported transport operation: {operation.GetType()}.");
				}
				if (!knownEvents.TryAdd(newEvent1.Event.EventId, null))
				{
					continue; // already applied/broadcast — e.g. an echo from this same client
				}

				await broadcastGate.WaitAsync(ctx.RequestAborted);
				try
				{
					var ev = newEvent1.Event;
					var projection = serviceKey is null
						? ctx.RequestServices.GetRequiredService<IProjection>()
						: ctx.RequestServices.GetRequiredKeyedService<IProjection>(serviceKey);
					await ev.AcceptAsync<EventVisitorContext?>((IEventVisitor<EventVisitorContext?>)projection, null!);
					var storage = serviceKey is null
						? ctx.RequestServices.GetRequiredService<IAppendStorage<Event, Guid>>()
						: ctx.RequestServices.GetRequiredKeyedService<IAppendStorage<Event, Guid>>(serviceKey);
					await storage.AppendAsync(ev);

					var buffer = ArrayPool<byte>.Shared.Rent(EventReplicationService.DefaultFrameSize);
					try
					{
						var payload = networkSerializationService.Serialize<TransportOperation>(new NewEvent1 { Event = ev }, buffer);
						foreach (var other in sockets)
						{
							if (other == socket || other.State != WebSocketState.Open) { continue; }
							try
							{
								await other.SendAsync(payload, networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ctx.RequestAborted);
							}
							catch
							{
								// best-effort broadcast — a dead peer socket shouldn't fail this client's request
							}
						}
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(buffer);
					}
				}
				catch
				{
					// One bad event must not kill this whole connection (every other
					// message on it, and the connection itself) — log and keep going.
				}
				finally
				{
					broadcastGate.Release();
				}
			}
		});

		return app;
	}

	static async Task<byte[]?> ReceiveFullMessageAsync(WebSocket ws, CancellationToken ct)
	{
		var rent = ArrayPool<byte>.Shared.Rent(EventReplicationService.DefaultFrameSize);
		try
		{
			using var ms = new MemoryStream(EventReplicationService.DefaultFrameSize);
			while (!ct.IsCancellationRequested)
			{
				var seg = new ArraySegment<byte>(rent);
				var res = await ws.ReceiveAsync(seg, ct);
				if (res.MessageType == WebSocketMessageType.Close)
				{
					return null;
				}
				if (res.Count > 0)
				{
					ms.Write(rent, 0, res.Count);
				}
				if (res.EndOfMessage)
				{
					break;
				}
			}
			return ms.ToArray();
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rent);
		}
	}
}
