using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
	/// <para>
	/// Pass <paramref name="readableStreamsResolver"/> to let each connection additionally
	/// <em>read</em> a host-chosen set of shared/public streams beyond its own writable stream
	/// (see <see cref="ReplicationStreamScope"/>). It is evaluated once per connection from the
	/// authenticated <see cref="HttpContext"/> and is never taken from the wire, so a client can
	/// only ever gain read access the host grants it — it still writes solely to its own stream.
	/// </para>
	/// </summary>
	public static WebApplication MapSynqraReplicationEndpoint(this WebApplication app, string path = "/api/synqra/ws", string? serviceKey = null, Func<HttpContext, IReadOnlySet<Guid>>? readableStreamsResolver = null)
	{
		app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

		// Each connected socket is tagged with its own writable stream plus the host-granted set of
		// additional readable streams, so a broadcast only reaches peers that may read the event's
		// stream (see the broadcast loop below). Was a ConcurrentBag<WebSocket> — a set with no
		// per-socket stream and, worse, no removal, so dead sockets accumulated forever. A dictionary
		// gives both the routing tags and O(1) removal on disconnect.
		var sockets = new ConcurrentDictionary<WebSocket, SocketScope>();
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

			// Additional streams this connection may READ (never write) — a host-chosen set of
			// shared/public streams resolved from the authenticated context, never from the wire.
			// null/empty means "own stream only", the pre-existing behavior.
			var readableStreams = readableStreamsResolver?.Invoke(ctx);
			var socketScope = new SocketScope(connectionStream, readableStreams);

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
				sockets[socket] = socketScope;
				await socket.SendAsync(magicBytes, WebSocketMessageType.Binary, endOfMessage: true, ctx.RequestAborted);

				var storage = serviceKey is null
					? ctx.RequestServices.GetRequiredService<IAppendStorage<Event, Guid>>()
					: ctx.RequestServices.GetRequiredKeyedService<IAppendStorage<Event, Guid>>(serviceKey);
				var backlogBuffer = ArrayPool<byte>.Shared.Rent(EventReplicationService.DefaultFrameSize);
				try
				{
					await foreach (var ev in storage.GetAllAsync(from: clientCursor, ctx.RequestAborted))
					{
						// Replay only streams THIS connection may read — its own stream plus any host-
						// granted readable stream. The log is one shared multitenant collection;
						// ev.StreamId (persisted as _sid) says which stream each event belongs to.
						// Without this filter the backlog leaked every stream's events to any
						// connecting client. Unscoped host (connectionStream null) → send all.
						if (!ReplicationStreamScope.Admits(ev.StreamId, connectionStream, readableStreams))
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
						foreach (var (other, otherScope) in sockets)
						{
							if (other == socket || other.State != WebSocketState.Open) { continue; }
							// Only fan out to peers that may READ the event's stream (their own stream
							// or a host-granted readable stream). Without this a client's event was
							// broadcast to every connected socket regardless of stream — a live
							// cross-tenant leak. The check is oriented per peer: does THIS peer admit
							// the event's stream (ev.StreamId, stamped above from the sender's stream)?
							// Unscoped peer (null own stream) → admits all, as before.
							if (!ReplicationStreamScope.Admits(ev.StreamId, otherScope.WritableStream, otherScope.ReadableStreams))
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

	/// <summary>Per-connection routing tags: the connection's own writable stream and the host-granted
	/// set of additional readable streams. Both feed <see cref="ReplicationStreamScope.Admits"/>.</summary>
	readonly record struct SocketScope(Guid? WritableStream, IReadOnlySet<Guid>? ReadableStreams);
}
