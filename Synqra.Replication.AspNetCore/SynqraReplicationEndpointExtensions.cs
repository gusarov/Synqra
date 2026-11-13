using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synqra;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;

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
	/// register those (e.g. AddSbxSerializer + AddMongoProjection +
	/// AddMongoEventStorage + AddServerEventLog) before calling this.
	/// <para>
	/// Pass <paramref name="serviceKey"/> when the process hosts more than one independent
	/// Synqra-backed feature — the endpoint will resolve <see cref="IProjection"/> and
	/// <see cref="IAppendStorage{Event, Guid}"/> from the keyed DI slot so each feature
	/// operates against its own store rather than sharing one global singleton.
	/// </para>
	/// <para>
	/// Pass <paramref name="readableStreamsResolver"/> to define, per connection, the set of
	/// shared/public streams the connection is <em>allowed</em> to read beyond its own writable
	/// stream (see <see cref="ReplicationStreamScope"/>). It is evaluated once per connection from
	/// the authenticated <see cref="HttpContext"/> and is never taken from the wire, so a client can
	/// only ever gain read access the host grants — it still writes solely to its own stream. This is
	/// the <em>authorization ceiling</em>; which of those streams are actually delivered is chosen by
	/// the client's HELLO subscription mode and live Subscribe/Unsubscribe frames.
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
		// Bounded: a long-running host sees every committed event through here, so an unbounded set would grow forever.
		var knownEvents = new RecentEventIds(capacity: 100_000);

		// Writes that never crossed a socket (a REST call, a background job) are pushed too: the
		// projection reports what each locally submitted command committed, and one ordered queue fans
		// those events out to every subscribed socket. Socket-borne events are applied directly and
		// never raised here, and knownEvents dedups anything seen twice.
		// The sequenced log (docs/model.md §9) reports every commit, socket-borne ones included, with the watermark it
		// was committed under; a host without one falls back to the projection's own signal and the socket path's fan-out.
		var log = serviceKey is null
			? app.Services.GetService<IEventLog>()
			: app.Services.GetKeyedService<IEventLog>(serviceKey);
		var watermarks = log as IEventWatermarks;
		var logBroadcasts = log is ICommittedEventSource;
		var seeding = serviceKey is null
			? app.Services.GetService<StreamSeeding>()
			: app.Services.GetKeyedService<StreamSeeding>(serviceKey);
		var guards = (serviceKey is null
			? app.Services.GetServices<IInboundEventGuard>()
			: app.Services.GetKeyedServices<IInboundEventGuard>(serviceKey)).ToArray();
		var projectionSource = (serviceKey is null
			? app.Services.GetService<IProjection>()
			: app.Services.GetKeyedService<IProjection>(serviceKey)) as ICommittedEventSource;
		// A cold client asks for the stream's state (plans/snapshot.md); a host with no source declines and the client replays.
		var snapshots = serviceKey is null
			? app.Services.GetService<IStreamSnapshotSource>()
			: app.Services.GetKeyedService<IStreamSnapshotSource>(serviceKey);
		var committedSources = new[] { log as ICommittedEventSource, projectionSource }.OfType<ICommittedEventSource>().Distinct().ToArray();
		if (committedSources.Length > 0)
		{
			var pushLogger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("Synqra.Replication.Endpoint");
			var queue = System.Threading.Channels.Channel.CreateUnbounded<(Event Event, Guid Watermark)>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, });
			Action<IEnumerable<Event>> enqueue = events =>
			{
				foreach (var ev in events)
				{
					queue.Writer.TryWrite((ev, watermarks?.ReadWatermark(ev.StreamId) ?? default)); // read under the commit's lock: the watermark of this very commit
				}
			};
			foreach (var source in committedSources)
			{
				source.EventsCommitted += enqueue;
			}
			var lifetime = app.Lifetime.ApplicationStopping;
			// The sources are singletons that can outlive this endpoint: stop feeding the queue on shutdown.
			lifetime.Register(() =>
			{
				foreach (var source in committedSources)
				{
					source.EventsCommitted -= enqueue;
				}
				queue.Writer.TryComplete();
			});
			var pushed = new RecentEventIds(capacity: 100_000); // the log and the projection both report a REST write: one push
			_ = Task.Run(async () =>
			{
				try
				{
					await foreach (var (ev, watermark) in queue.Reader.ReadAllAsync(lifetime))
					{
						if (pushed.TryAdd(ev.EventId))
						{
							// Marking a committed event lets the socket path drop an echo; a backlog replay that marked
							// one first must not suppress it here, hence the separate pushed set above.
							knownEvents.TryAdd(ev.EventId);
							// One event's failure must not end the push for every event after it.
							try
							{
								await BroadcastAsync(ev, watermark);
							}
							catch (Exception ex) when (ex is not OperationCanceledException)
							{
								pushLogger?.LogError(ex, "Synqra replication: pushing committed event {EventId} failed; the push loop continues.", ev.EventId);
							}
						}
					}
				}
				catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
				{
					// host shutting down
				}

				// Queued to each peer that reads the event's stream, never sent from here: a peer's network holds up only its own pushes.
				// Under the gate only to read the peers' subscriptions while no Subscribe frame changes them.
				async Task BroadcastAsync(Event ev, Guid watermark)
				{
					await broadcastGate.WaitAsync(lifetime);
					try
					{
						foreach (var (peer, peerScope) in sockets)
						{
							if (peer.State == WebSocketState.Open && ReplicationStreamScope.Admits(ev.StreamId, peerScope.WritableStream, peerScope.Active))
							{
								peerScope.Outbox.Push(ev, watermark);
							}
						}
					}
					finally
					{
						broadcastGate.Release();
					}
				}
			});
		}

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

			// Additional streams this connection may read — a host-chosen ceiling resolved from the
			// authenticated context, never from the wire. Combined with the own stream this forms the
			// "grantable" set: the most a client could ever be subscribed to. null/empty resolver means
			// "own stream only". Only the read (event-delivery) path exists today; submitting events
			// to a subscribed stream is a future replication-API concern tracked separately, not an
			// inherent restriction of these streams.
			var granted = readableStreamsResolver?.Invoke(ctx);
			var grantable = new HashSet<Guid>();
			if (connectionStream is Guid ownStream)
			{
				grantable.Add(ownStream);
			}
			if (granted is not null)
			{
				foreach (var g in granted)
				{
					grantable.Add(g);
				}
			}

			// A replay frame that cannot be written ends this connection: the failure names the event, so the log says which one.
			async Task ReplayFrameAsync(ReplicationOutbox outbox, Event ev, Guid watermark)
			{
				try
				{
					await outbox.SendAsync(new EventEnvelope { Event = ev, Watermark = watermark }, ctx.RequestAborted);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					throw new InvalidOperationException($"Synqra replication: replaying event {ev.EventId} of stream {ev.StreamId} to a client failed.", ex);
				}
			}

			// The watermark taken under the gate BEFORE the read caps every backlog frame's resume point: anything committed
			// after it is either in the read or queued behind the gate for this socket, so a client resuming from it misses
			// nothing. Seeded from the log's tail, so a process that has not written the stream since it started hands out a
			// real position (an empty one would leave the client replaying the same backlog on every connect).
			async Task<IReadOnlyDictionary<Guid, Guid>> ReadWatermarksAsync(IEnumerable<Guid> streams)
			{
				var taken = new Dictionary<Guid, Guid>();
				foreach (var stream in streams)
				{
					taken[stream] = watermarks is null ? default : await watermarks.ReadWatermarkAsync(stream, ctx.RequestAborted);
				}
				return taken;
			}

			// The master seeds a stream it finds empty, or evolves an older seed, before a subscriber reads it, so that subscriber's replay
			// carries it and no client ever writes a seed for a stream it did not create (docs/model.md §10). Under the broadcast gate, like the read.
			async Task SeedOrUpgradeAsync(IEventLog storage, Guid stream)
			{
				if (seeding is { HasSeeders: true })
				{
					await seeding.SeedOrUpgradeAsync(stream, storage, await ResolveSeedStoreAsync(stream), ctx.RequestAborted);
				}
			}

			// The first guard that refuses a peer's event names why; none refusing lets it through.
			async Task<string?> AskGuardsAsync(Event ev)
			{
				string? reason = null;
				for (var i = 0; i < guards.Length && reason is null; i++)
				{
					reason = await guards[i].RefuseAsync(ev, ctx.RequestAborted);
				}
				return reason;
			}

			// The stream's state cut at a watermark (plans/snapshot.md), or why it is declined: the host serves none, the connection is
			// unscoped, the stream holds nothing or the cut fails. The caller holds the global gate, so no peer's event is between being
			// applied and being logged: the state and the log agree at the watermark. Sending it is SendSnapshotAsync's, outside the gate.
			async Task<SnapshotTaken> CutSnapshotAsync(Guid stream)
			{
				SnapshotCut? cut = null;
				var declined = snapshots is null ? "this master serves no snapshots"
					: connectionStream is null ? "an unscoped connection replays its streams"
					: null;
				if (declined is null)
				{
					try
					{
						cut = await snapshots!.CutAsync(stream, ctx.RequestAborted);
						if (cut is null)
						{
							declined = "the stream holds nothing to snapshot";
						}
						else if (cut.Watermark == default)
						{
							declined = "the stream's log reports no watermark to resume after";
						}
					}
					catch (Exception ex) when (ex is not OperationCanceledException)
					{
						logger?.LogError(ex, "Synqra replication: the snapshot of stream {Stream} could not be cut; the client replays the stream instead.", stream);
						declined = $"the snapshot failed: {ex.Message}";
					}
				}
				return new SnapshotTaken(stream, declined is null ? cut : null, declined, cut);
			}

			// SnapshotBegin (the schema header), its chunks, SnapshotEnd; or SnapshotDeclined with the reason, and the client takes the ordinary
			// backlog. Returns the watermark the backlog resumes after.
			async Task<Guid?> SendSnapshotAsync(ReplicationOutbox outbox, SnapshotTaken taken)
			{
				Guid? sent = null;
				var declined = taken.Declined;
				if (taken.Cut is { } cut)
				{
					try
					{
						await SendCutAsync(outbox, taken.Stream, cut);
						sent = cut.Watermark;
					}
					catch (Exception ex) when (ex is not OperationCanceledException)
					{
						// The client drops whatever part it got on the decline and replays: slower, never wrong.
						logger?.LogError(ex, "Synqra replication: the snapshot of stream {Stream} failed; the client replays the stream instead.", taken.Stream);
						declined = $"the snapshot failed: {ex.Message}";
					}
				}
				if (declined is not null)
				{
					await outbox.SendAsync(new SnapshotDeclined { StreamId = taken.Stream, Reason = declined }, ctx.RequestAborted);
				}
				return sent;
			}

			// The chunks are written before the header is sent: the header names exactly the types they use, never the host's whole map.
			async Task SendCutAsync(ReplicationOutbox outbox, Guid stream, SnapshotCut cut)
			{
				var schema = new SnapshotSchema(ctx.RequestServices.GetRequiredService<ISbxSerializerFactory>());
				var writer = schema.CreateWriter();
				var snapshotId = Guid.CreateVersion7();
				var chunks = new SnapshotChunkWriter(writer, snapshotId);
				var written = new List<SnapshotChunk>();
				await foreach (var record in cut.Records.WithCancellation(ctx.RequestAborted))
				{
					if (chunks.Write(record) is { } chunk)
					{
						written.Add(chunk);
					}
				}
				if (chunks.Complete() is { } last)
				{
					written.Add(last);
				}
				await outbox.SendAsync(schema.DescribeSnapshot(writer, stream, snapshotId, cut.Watermark, entityCount: chunks.RecordCount), ctx.RequestAborted);
				foreach (var chunk in written)
				{
					await outbox.SendAsync(chunk, ctx.RequestAborted);
				}
				await outbox.SendAsync(new SnapshotEnd
				{
					SnapshotId = snapshotId,
					Watermark = cut.Watermark,
					ChunkCount = chunks.ChunkCount,
					RecordCount = chunks.RecordCount,
				}, ctx.RequestAborted);
				logger?.LogInformation("Synqra replication: snapshot {SnapshotId} of stream {Stream} at {Watermark}: {Records} records in {Chunks} chunks.", snapshotId, stream, cut.Watermark, chunks.RecordCount, chunks.ChunkCount);
			}

			// The host's store for the stream: its multitenant IObjectStore (Mongo), else the stream's projection from the provider.
			async Task<IObjectStore> ResolveSeedStoreAsync(Guid stream)
			{
				var store = serviceKey is null
					? ctx.RequestServices.GetService<IObjectStore>()
					: ctx.RequestServices.GetKeyedService<IObjectStore>(serviceKey);
				if (store is null)
				{
					var provider = (serviceKey is null
						? ctx.RequestServices.GetService<IProjectionProvider>()
						: ctx.RequestServices.GetKeyedService<IProjectionProvider>(serviceKey))
						?? throw new InvalidOperationException("Synqra replication: stream seeders are registered, but the host has no IObjectStore or IProjectionProvider to write a seed through.");
					store = await provider.GetAsync(stream, ctx.RequestAborted);
				}
				return store;
			}

			#region HELLO — from client
			// 8 bytes magic + 16 bytes the client's own "last event id it already received from a
			// server" cursor (Guid.Empty means "never synced, send me everything") — see
			// EventReplicationState.ServerWatermark's remarks — plus the HELLO "ws-method":
			//   byte[24] kind: see ReplicationHelloKind (0 is reserved/invalid).
			//   For kind 2 the next 16 bytes are the stream id to subscribe to (total 41 bytes).
			// A legacy 24-byte HELLO omits the kind and is treated as UserDefaultMainStream.
			var helloBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
			if (helloBytes is null)
			{
				return;
			}
			if (helloBytes.Length is not (24 or 25 or 41))
			{
				throw new InvalidOperationException($"Protocol negotiation failed: received {helloBytes.Length} bytes (expected 24, 25, or 41).");
			}
			var magic = BitConverter.ToUInt64(helloBytes, 0);
			if (magic != networkSerializationService.Magic)
			{
				throw new InvalidOperationException($"Protocol negotiation failed: received magic {magic:X16} instead of {networkSerializationService.Magic:X16}.");
			}
			var clientCursor = new Guid(helloBytes.AsSpan(8, 16));
			var helloKind = helloBytes.Length >= 25 ? (ReplicationHelloKind)helloBytes[24] : ReplicationHelloKind.UserDefaultMainStream;
			// The streams this connection is currently subscribed to. Seeded from the HELLO kind, then
			// mutated live by Subscribe/Unsubscribe frames. Only ever touched under broadcastGate, so
			// the broadcast loop's reads never race a subscription change.
			var active = new HashSet<Guid>();
			switch (helloKind)
			{
				case ReplicationHelloKind.NoAutoSubscription:
					break;
				case ReplicationHelloKind.UserDefaultMainStream:
					if (connectionStream is Guid ownAtHello)
					{
						active.Add(ownAtHello);
					}
					break;
				case ReplicationHelloKind.SubscribeTo:
				case ReplicationHelloKind.SubscribeToSnapshot:
					if (helloBytes.Length != 41)
					{
						throw new InvalidOperationException($"Protocol negotiation failed: Hello_{helloKind} requires a 16-byte stream id (41-byte HELLO).");
					}
					var helloTarget = new Guid(helloBytes.AsSpan(25, 16));
					// Authorize against the ceiling — an unscoped host (no connectionStream) admits any
					// stream; a scoped host admits only streams in the grantable set. A rejected target
					// simply leaves the active set empty; the ack below reveals what actually took.
					if (connectionStream is null || grantable.Contains(helloTarget))
					{
						active.Add(helloTarget);
					}
					break;
				default:
					throw new InvalidOperationException($"Protocol negotiation failed: unknown HELLO kind {helloBytes[24]}.");
			}
			#endregion

			#region HELLO — to client, then the catch-up: the snapshots, and the backlog since clientCursor
			// The socket is registered for pushes BEFORE anything is read for it, with its pushes held: an event committed after a read is
			// either in that read or in this socket's outbox, and the outbox writes it only after the catch-up, so a push never overtakes
			// the backlog before it. Only the bookkeeping runs under the global gate (registering, seeding, cutting the snapshots, reading
			// the watermarks), which orders it with the inbound path that applies and logs a peer's event under the same gate. The sends
			// (the HELLO reply, the snapshots, the backlog) run outside it: a slow client holds up only its own pushes.
			var outbox = new ReplicationOutbox(socket, networkSerializationService, logger);
			var socketScope = new SocketScope(connectionStream, grantable, active, outbox);
			var storage = serviceKey is null
				? ctx.RequestServices.GetRequiredService<IEventLog>()
				: ctx.RequestServices.GetRequiredKeyedService<IEventLog>(serviceKey);
			var magicBytes = BitConverter.GetBytes(networkSerializationService.Magic);
			var pump = outbox.PumpAsync(ctx.RequestAborted);
			try
			{
			using (await outbox.HoldPushesAsync(ctx.RequestAborted))
			{
				var taken = new List<SnapshotTaken>();
				IReadOnlyDictionary<Guid, Guid> backlogWatermarks;
				var snapshotted = new Dictionary<Guid, Guid>();
				try // every cut taken is disposed, whatever throws after it: a Mongo cut holds a snapshot session open
				{
					await broadcastGate.WaitAsync(ctx.RequestAborted);
					try
					{
						sockets[socket] = socketScope;
						foreach (var stream in socketScope.Active)
						{
							await SeedOrUpgradeAsync(storage, stream);
						}
						// A cold client gets each stream's state as of a watermark, and the backlog after it below.
						if (helloKind == ReplicationHelloKind.SubscribeToSnapshot)
						{
							foreach (var stream in socketScope.Active)
							{
								taken.Add(await CutSnapshotAsync(stream));
							}
						}
						backlogWatermarks = await ReadWatermarksAsync(socketScope.Active);
					}
					finally
					{
						broadcastGate.Release();
					}

					await outbox.SendBytesAsync(magicBytes, ctx.RequestAborted);
					// Auto-ack the HELLO with the authoritative active set, so the client can immediately
					// tell whether the server's default matched what it expected (see SubscriptionState).
					await outbox.SendAsync(new SubscriptionState { Streams = socketScope.Active.ToList() }, ctx.RequestAborted);
					foreach (var snapshot in taken)
					{
						if (await SendSnapshotAsync(outbox, snapshot) is Guid cutAt)
						{
							snapshotted[snapshot.Stream] = cutAt;
						}
					}
				}
				finally
				{
					foreach (var snapshot in taken)
					{
						if (snapshot.Owned is { } owned)
						{
							await owned.DisposeAsync();
						}
					}
				}

				// Replay only the streams THIS connection is subscribed to, each strictly after the client's watermark (or the
				// snapshot's): a stream is a key prefix of the multitenant log, so a scoped connection reads exactly its own
				// streams. An unscoped host (connectionStream null) sends every stream's events after the watermark.
				var backlog = connectionStream is null
					? AfterCursor(storage.GetAllAsync(default, ctx.RequestAborted), clientCursor)
					: ReadStreams(storage, socketScope.Active.ToArray(), stream => snapshotted.TryGetValue(stream, out var cutAt) ? cutAt : clientCursor, ctx.RequestAborted);
				await foreach (var ev in backlog)
				{
					knownEvents.TryAdd(ev.EventId);
					await ReplayFrameAsync(outbox, ev, ev.SequenceId.CapAt(backlogWatermarks.TryGetValue(ev.StreamId, out var w) ? w : default));
				}
			} // the pushes held meanwhile follow
			#endregion

			while (!ctx.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
			{
				var messageBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
				if (messageBytes is null)
				{
					break;
				}
				if (messageBytes.Length == 0)
				{
					continue;
				}
				var operation = networkSerializationService.Deserialize<TransportOperation>(messageBytes);

				// Subscribe / Unsubscribe control messages. Mutate this connection's active set (under
				// the broadcast gate so the fan-out loop never races it), replay the newly-subscribed
				// stream's backlog with this socket's pushes held, as at HELLO, then ack the resulting active set.
				if (operation is SubscribeRequest or UnsubscribeRequest)
				{
					var target = operation is SubscribeRequest sub ? sub.StreamId : ((UnsubscribeRequest)operation).StreamId;
					using (await outbox.HoldPushesAsync(ctx.RequestAborted))
					{
						Guid? subWatermark = null;
						List<Guid> subscribed;
						await broadcastGate.WaitAsync(ctx.RequestAborted);
						try
						{
							if (operation is SubscribeRequest)
							{
								// Authorize against the ceiling: unscoped host admits any stream, scoped host
								// only its grantable set. A rejected target leaves the active set unchanged —
								// the ack tells the client it did not take.
								if ((connectionStream is null || grantable.Contains(target)) && socketScope.Active.Add(target))
								{
									await SeedOrUpgradeAsync(storage, target);
									subWatermark = watermarks is null ? default : await watermarks.ReadWatermarkAsync(target, ctx.RequestAborted);
								}
							}
							else
							{
								socketScope.Active.Remove(target);
							}
							subscribed = socketScope.Active.ToList();
						}
						finally
						{
							broadcastGate.Release();
						}
						if (subWatermark is Guid replayCap)
						{
							await foreach (var ev in storage.ReadStreamAsync(target, default, ctx.RequestAborted))
							{
								knownEvents.TryAdd(ev.EventId);
								await ReplayFrameAsync(outbox, ev, ev.SequenceId.CapAt(replayCap));
							}
						}
						await outbox.SendAsync(new SubscriptionState { Streams = subscribed }, ctx.RequestAborted);
					}
					continue;
				}

				if (operation is not EventEnvelope eventEnvelope)
				{
					throw new NotSupportedException($"Unsupported transport operation: {operation?.GetType()}.");
				}
				// Applied and logged under the global gate; the reply is sent after it, through this socket's outbox.
				TransportOperation? reply = null;
				await broadcastGate.WaitAsync(ctx.RequestAborted);
				try
				{
					var ev = eventEnvelope.Event;
					// Already applied/broadcast: an echo from this same client, or a resend after a lost ack. The in-memory set is
					// empty after a restart, so the log is asked too: an event it holds was applied by the process that stored it,
					// and applying it again is refused by the projection (prod 2026-10-22: a facet re-added to a filled slot, on
					// every connect, since a refusal gets no ack). Either way it is acknowledged again, never re-applied.
					var known = !knownEvents.TryAdd(ev.EventId);
					if (!known && connectionStream is Guid heldStream && storage is IEventLookup lookup)
					{
						known = await lookup.ContainsAsync(heldStream, ev.EventId, ctx.RequestAborted);
						if (known)
						{
							logger?.LogInformation("Synqra replication: event {EventId} of stream {Stream} is in the log already; acknowledged, not applied again.", ev.EventId, heldStream);
						}
					}
					if (known)
					{
						reply = new EventAccepted { EventId = ev.EventId };
					}
					else
					{
						// The event's stream is set authoritatively from THIS connection's authorized
						// stream — never trusted from the wire (Event.StreamId is not even on the SBX
						// wire format; it arrives default). This both stamps _sid correctly on the
						// durable append and forbids a client from injecting an event into any other
						// stream: whatever it claims, the event is filed under the stream it connected as.
						if (connectionStream is Guid stream)
						{
							ev.StreamId = stream;
						}
						ev.SequenceId = default; // a sequence is a position in the log that stamped it: the client's local one means nothing here
						if ((ev is StreamSnapshotEvent ? "a stream snapshot is a client's own record, never an event of the stream" : await AskGuardsAsync(ev)) is { } refused)
						{
							// The host forbids this change from a peer (a system object, say): refused for good, never applied or stored.
							logger?.LogInformation("Synqra replication: event {EventId} on stream {Stream} refused by a guard: {Reason}", ev.EventId, connectionStream, refused);
							reply = new EventRejected { EventId = ev.EventId, Reason = refused };
						}
						else
						{
							var projection = serviceKey is null
								? ctx.RequestServices.GetRequiredService<IProjection>()
								: ctx.RequestServices.GetRequiredKeyedService<IProjection>(serviceKey);
							if (projection is IReplayProjection replay)
							{
								await replay.ApplyAsync(ev); // under the projection's write gate, like a submit
							}
							else
							{
								await ev.AcceptAsync<EventVisitorContext?>((IEventVisitor<EventVisitorContext?>)projection, null!); // Mongo gates each visitor per stream
							}
							await storage.AppendAsync(ev); // a sequenced log reports this commit to the push loop, which echoes it to the sender too
							// The sender moves its outbound cursor on this, not on having written the frame: a refused event gets no ack and is offered again.
							reply = new EventAccepted { EventId = ev.EventId };
							if (!logBroadcasts)
							{
								// Only fan out to peers subscribed to the event's stream. Without this a
								// client's event was broadcast to every connected socket regardless of stream
								// — a live cross-tenant leak. The check is oriented per peer: is THIS peer
								// currently subscribed to the event's stream (ev.StreamId, stamped above from
								// the sender's stream)? Unscoped peer (null own stream) → admits all, as before.
								// Queued to each peer's outbox, never sent from here.
								foreach (var (other, otherScope) in sockets)
								{
									if (other != socket && other.State == WebSocketState.Open && ReplicationStreamScope.Admits(ev.StreamId, otherScope.WritableStream, otherScope.Active))
									{
										otherScope.Outbox.Push(ev, default);
									}
								}
							}
						}
					}
				}
				catch (InvalidOperationException refusal)
				{
					// The projection refused the event (a facet for a slot already filled, a link that exists): it will never be
					// in this log, so the sender is told and moves its cursor past it instead of offering it on every connect.
					logger?.LogWarning(refusal, "Synqra replication: event {EventId} on stream {Stream} refused; the sender is told and the connection continues.", eventEnvelope.Event.EventId, connectionStream);
					reply = new EventRejected { EventId = eventEnvelope.Event.EventId, Reason = refusal.Message };
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
				if (reply is not null)
				{
					await outbox.SendAsync(reply, ctx.RequestAborted);
				}
			}
			}
			finally
			{
				// Stop tracking a closed socket — otherwise the registry (and the broadcast loop
				// that walks it) grew without bound across the server's lifetime.
				sockets.TryRemove(socket, out _);
				outbox.Complete();
				await pump;
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

	/// <summary>Per-connection routing state: the connection's own writable stream, the host-authorized
	/// ceiling of streams it MAY read (<see cref="Grantable"/>), and the subset it is currently
	/// subscribed to (<see cref="Active"/>, mutated live by Subscribe/Unsubscribe frames, only ever under
	/// the endpoint's broadcast gate). <see cref="Active"/> feeds <see cref="ReplicationStreamScope.Admits"/>.</summary>
	sealed class SocketScope
	{
		public SocketScope(Guid? writableStream, IReadOnlySet<Guid> grantable, HashSet<Guid> active, ReplicationOutbox outbox)
		{
			WritableStream = writableStream;
			Grantable = grantable;
			Active = active;
			Outbox = outbox;
		}

		public Guid? WritableStream { get; }
		public IReadOnlySet<Guid> Grantable { get; }
		public HashSet<Guid> Active { get; }

		/// <summary>Every send to this socket goes through here: pushes are queued, never sent by the broadcaster.</summary>
		public ReplicationOutbox Outbox { get; }
	}

	/// <summary>
	/// A stream's snapshot as cut under the gate, sent after it: the <see cref="Cut"/> to send, or why it is <see cref="Declined"/>. The
	/// <see cref="Owned"/> cut, whether sent or not, holds a read (a Mongo snapshot session) until it is disposed.
	/// </summary>
	sealed record SnapshotTaken(Guid Stream, SnapshotCut? Cut, string? Declined, SnapshotCut? Owned);

	/// <summary>
	/// A backlog frame's resume point: the frame's own sequence, capped by the watermark taken at connect. A client persists
	/// it once it holds the frame, so a run that ends mid-replay resumes exactly there. The watermark itself, sent on every
	/// frame, named the tail before the client held it (prod 2026-10-22: a reload mid-replay left a hole no connect filled).
	/// </summary>
	static Guid CapAt(this Guid sequence, Guid watermark) => sequence.CompareBigEndian(watermark) < 0 ? sequence : watermark;

	/// <summary>Each stream's events strictly after its cursor, stream by stream.</summary>
	static async IAsyncEnumerable<Event> ReadStreams(this IEventLog log, Guid[] streams, Func<Guid, Guid> cursorOf, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		foreach (var stream in streams)
		{
			await foreach (var ev in log.ReadStreamAsync(stream, cursorOf(stream), cancellationToken))
			{
				yield return ev;
			}
		}
	}

	/// <summary>The events whose sequence is past the cursor (all of them for a cold cursor).</summary>
	static async IAsyncEnumerable<Event> AfterCursor(this IAsyncEnumerable<Event> events, Guid cursor)
	{
		await foreach (var ev in events)
		{
			if (cursor == default || ev.SequenceId.CompareBigEndian(cursor) > 0)
			{
				yield return ev;
			}
		}
	}
}
