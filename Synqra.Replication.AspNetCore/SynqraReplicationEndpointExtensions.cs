using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synqra;
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

		// Each connected socket is tagged with the stream it is authorized for, so a broadcast
		// only reaches peers on the same stream (see the broadcast loop below). Was a
		// ConcurrentBag<WebSocket> — a set with no per-socket stream and, worse, no removal, so
		// dead sockets accumulated forever. A dictionary gives both the stream tag and O(1)
		// removal on disconnect.
		var sockets = new ConcurrentDictionary<WebSocket, Guid?>();
		var broadcastGate = new SemaphoreSlim(1, 1);
		var knownEvents = new ConcurrentDictionary<Guid, object?>();

		app.Map(path, async ctx =>
		{
			var networkSerializationService = ctx.RequestServices.GetRequiredService<INetworkSerializationService>();
			var logger = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Synqra.Replication.Endpoint");
			if (!ctx.WebSockets.IsWebSocketRequest)
			{
				ctx.Response.StatusCode = 400;
				return;
			}
			using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

			// The stream this connection is authorized for, taken from the ambient
			// SynqraStreamContext the host established for the request (for Quotaly that is the
			// authenticated user's own stream, set by the auth middleware that wraps this whole
			// connection's read loop). It is NEVER taken from anything the client sends — a client
			// must not be able to name another user's stream. null means the host set no scope at
			// all (a single-tenant deployment): everything below then behaves as before, unscoped.
			var connectionStream = SynqraStreamContext.CurrentOrNull;

			#region HELLO — from client
			// 8 bytes magic + 16 bytes the client's own "last event id it already received
			// from a server" cursor (Guid.Empty means "never synced, send me everything") —
			// see EventReplicationState.LastEventIdFromServer's own remarks.
			var helloBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
			if (helloBytes is null)
			{
				return;
			}
			if (helloBytes.Length != 24)
			{
				throw new InvalidOperationException($"Protocol negotiation failed: received {helloBytes.Length} bytes instead of 24.");
			}
			var magic = BitConverter.ToUInt64(helloBytes, 0);
			if (magic != networkSerializationService.Magic)
			{
				throw new InvalidOperationException($"Protocol negotiation failed: received magic {magic:X16} instead of {networkSerializationService.Magic:X16}.");
			}
			var clientCursor = new Guid(helloBytes.AsSpan(8, 16));
			#endregion

			#region HELLO — to client, then replay this stream's backlog since clientCursor
			// Register for broadcasts BEFORE answering HELLO, and do both — plus the backlog
			// replay below — under the same gate as every broadcast SendAsync on this socket:
			// the moment the client observes the HELLO reply, every later broadcast is
			// guaranteed to reach it, and none of this can interleave with a concurrent
			// broadcast write. Holding the gate for the whole backlog replay blocks other
			// clients' broadcasts briefly — acceptable, this runs once per connection, and
			// the file already accepts a single global semaphore over throughput elsewhere.
			var magicBytes = BitConverter.GetBytes(networkSerializationService.Magic);
			await broadcastGate.WaitAsync(ctx.RequestAborted);
			try
			{
				sockets[socket] = connectionStream;
				await socket.SendAsync(magicBytes, WebSocketMessageType.Binary, endOfMessage: true, ctx.RequestAborted);

				var storage = serviceKey is null
					? ctx.RequestServices.GetRequiredService<IAppendStorage<Event, Guid>>()
					: ctx.RequestServices.GetRequiredKeyedService<IAppendStorage<Event, Guid>>(serviceKey);
				var backlogBuffer = ArrayPool<byte>.Shared.Rent(EventReplicationService.DefaultFrameSize);
				try
				{
					await foreach (var ev in storage.GetAllAsync(from: clientCursor, ctx.RequestAborted))
					{
						// Replay only THIS connection's stream. The log is one shared multitenant
						// collection; ev.StreamId (persisted as _sid) says which stream each event
						// belongs to. Without this filter the backlog leaked every stream's events
						// to any connecting client. Unscoped host (connectionStream null) → send all.
						if (!ReplicationStreamScope.Admits(ev.StreamId, connectionStream))
						{
							continue;
						}
						knownEvents.TryAdd(ev.EventId, null);
						var payload = networkSerializationService.Serialize<TransportOperation>(new NewEvent1 { Event = ev }, backlogBuffer);
						await socket.SendAsync(payload, networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ctx.RequestAborted);
					}
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(backlogBuffer);
				}
			}
			finally
			{
				broadcastGate.Release();
			}
			#endregion

			try
			{

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
					// The event's stream is set authoritatively from THIS connection's authorized
					// stream — never trusted from the wire (Event.StreamId is not even on the SBX
					// wire format; it arrives default). This both stamps _sid correctly on the
					// durable append and forbids a client from injecting an event into any other
					// stream: whatever it claims, the event is filed under the stream it connected as.
					if (connectionStream is Guid stream)
					{
						ev.StreamId = stream;
					}
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
						foreach (var (other, otherStream) in sockets)
						{
							if (other == socket || other.State != WebSocketState.Open) { continue; }
							// Only fan out to peers on the same stream as the event. Without this a
							// client's event was broadcast to every connected socket regardless of
							// stream — a live cross-tenant leak. Unscoped host (connectionStream null)
							// → broadcast to all, as before. (connectionStream == ev.StreamId here,
							// since inbound events were stamped with it above.)
							if (!ReplicationStreamScope.Admits(otherStream, connectionStream))
							{
								continue;
							}
							try
							{
								await other.SendAsync(payload, networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ctx.RequestAborted);
							}
							catch (Exception broadcastEx)
							{
								// best-effort broadcast — a dead peer socket shouldn't fail this client's request
								logger?.LogDebug(broadcastEx, "Synqra replication: dropping event {EventId} to a peer socket failed (peer likely gone).", ev.EventId);
							}
						}
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(buffer);
					}
				}
				catch (Exception eventEx)
				{
					// One bad event must not kill this whole connection (every other
					// message on it, and the connection itself) — log and keep going.
					logger?.LogWarning(eventEx, "Synqra replication: failed to apply/broadcast an inbound event on stream {Stream}; connection continues.", connectionStream);
				}
				finally
				{
					broadcastGate.Release();
				}
			}
			}
			finally
			{
				// Stop tracking a closed socket — otherwise the registry (and the broadcast loop
				// that walks it) grew without bound across the server's lifetime.
				sockets.TryRemove(socket, out _);
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
