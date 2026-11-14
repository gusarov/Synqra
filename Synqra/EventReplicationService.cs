using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using System;
using System.Buffers;
using System.Diagnostics;
using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

/// <summary>
/// Client-side service that connect to WS Master
/// </summary>
public class EventReplicationService : BackgroundService, IEventReplicationService
{
	public const int DefaultFrameSize = 8192;

	private readonly IEventLog _storage;
	private readonly EventReplicationState _eventReplicationState;
	private readonly JsonSerializerContext? _jsonSerializerContext;
	private readonly EventReplicationConfig _config;

	private readonly INetworkSerializationService _networkSerializationService;
	private readonly IEventReplicationStateStore? _stateStore;
	private readonly ISbxSerializerFactory? _serializerFactory;
	private readonly bool _foldsSnapshots;

	private SemaphoreSlim _autoResetEvent = new SemaphoreSlim(0, 1);
	private CancellationTokenSource _cts = new CancellationTokenSource();

	volatile bool _isOnline;
	public bool IsOnline
	{
		get => _isOnline;
		private set
		{
			if (_isOnline != value)
			{
				_isOnline = value;
				_isCaughtUp &= value; // an ended connection is caught up no more: the next one catches up again
				StatusChanged?.Invoke();
			}
		}
	}

	volatile bool _isCaughtUp;

	/// <inheritdoc />
	public bool IsCaughtUp => _isCaughtUp;

	/// <inheritdoc />
	public event Action? StatusChanged;

	// The probe this connection sent after its HELLO: only the CaughtUp that answers it ends the catch-up.
	Guid _catchUpRequest;

	// Guards the pause and the attempt that ReconnectNow cuts short, and the next attempt's due time.
	readonly object _reconnectGate = new object();
	// The pause before the next connect attempt, while one runs.
	CancellationTokenSource? _pause;
	// The connect attempt under way, then the connection it opened, until that connection ends; cancelling it aborts the socket.
	CancellationTokenSource? _attempt;
	// Set by ReconnectNow: the next pause is skipped.
	bool _reconnectAtOnce;
	DateTimeOffset? _nextAttemptAt;
	// Who cut the current connection short, if anyone: its reader's end is then expected, not a failure.
	volatile string? _cutBy;
	// Whether the watchdog already counted this connection's cut toward refusing snapshots.
	volatile bool _cutCounted;
	// Since when the reader waits for a frame (a Stopwatch timestamp), or 0 while it handles one: the catch-up watchdog's clock.
	long _waitingSince;
	// Whether this connection's HELLO asked for a snapshot whose end or decline has not arrived yet.
	volatile bool _awaitingSnapshot;

	/// <inheritdoc />
	public DateTimeOffset? NextAttemptAt
	{
		get
		{
			lock (_reconnectGate)
			{
				return _nextAttemptAt;
			}
		}
	}

	/// <inheritdoc />
	public long ConnectionFrames { get; private set; }

	/// <inheritdoc />
	public Guid ServerWatermark => _eventReplicationState.ServerWatermark;

	private Task? _readerTask;
	private ClientWebSocket? _wsConnection;

	// The last event written to the socket this run; the persisted cursor (LastEventIdFromMe) moves only when the master acknowledges one.
	private Guid _lastSentEventId;
	// Its position in the local log once this run has passed it, and its stream: later sends read only what follows.
	private Guid _lastSentSequence;
	private Guid _lastSentStream;
	private volatile IReadOnlyCollection<Guid> _activeStreams = Array.Empty<Guid>();

	/// <inheritdoc />
	public IReadOnlyCollection<Guid> ActiveStreams => _activeStreams;

	/// <inheritdoc />
	public event Action? EventsReceived;

	/// <summary>Event frames this run received from the server, known ones included: what a resumed client was sent again.</summary>
	public long FramesReceived { get; private set; }

	/// <summary>Event frames this run wrote to the server: a client never echoes what the master sent it.</summary>
	public long FramesSent { get; private set; }

	/// <summary>Stream snapshots this run received and stored in the local log (plans/snapshot.md).</summary>
	public long SnapshotsLoaded { get; private set; }

	// The snapshot being received on this connection: its header, then its chunks, until its end.
	SnapshotBegin? _snapshotHeader;
	readonly List<SnapshotChunk> _snapshotChunks = new List<SnapshotChunk>();
	// Set when this run gives up on snapshots and replays instead: a header this build cannot read, a snapshot that broke the
	// protocol (a chunk out of place, counts that do not add up), a second connection lost in the middle of one, or a master that
	// did not take the stream a snapshot request named. Without it a deterministic failure would be asked for again forever.
	bool _snapshotRefused;
	// Connections that ended before a snapshot was in, since the last one stored (a ReconnectNow's cut aside): one is a dropped
	// connection, the second is a pattern.
	int _snapshotsCutShort;
	// Whether this connection's HELLO asked for a snapshot in place of the configured UserDefaultMainStream: it names the stream
	// where the configured kind lets the master choose it, so the master must confirm it took that stream.
	bool _helloNamedStream;
	// Per stream, the watermark of the snapshot the log holds: an event of that stream the master stamped at or before it is in
	// that state already. Another stream's sequences say nothing about it.
	readonly Dictionary<Guid, Guid> _snapshotWatermarks = new Dictionary<Guid, Guid>();

	// Ids the master sent this run: it holds them, so the send loop steps over them instead of echoing them back.
	readonly HashSet<Guid> _heldByMaster = new HashSet<Guid>();

	// The command of each own event this run sent and the master has not answered yet: its verdict names it. Under _skipSet.
	readonly Dictionary<Guid, Guid> _awaitingVerdict = new Dictionary<Guid, Guid>();

	// Own events the master refused, and their undo. The reader alone uses it.
	readonly RefusedEvents _refused = new RefusedEvents();

	/// <inheritdoc />
	public event Action<EventVerdict>? EventSettled;

	/// <inheritdoc />
	public Task SubscribeAsync(Guid streamId, CancellationToken ct = default)
		=> SendOperationAsync(new SubscribeRequest { StreamId = streamId }, ct);

	/// <inheritdoc />
	public Task UnsubscribeAsync(Guid streamId, CancellationToken ct = default)
		=> SendOperationAsync(new UnsubscribeRequest { StreamId = streamId }, ct);

	private async Task SendOperationAsync(TransportOperation operation, CancellationToken ct)
	{
		var socket = _wsConnection ?? throw new InvalidOperationException("Not connected — cannot change subscriptions before the replication client has connected.");
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
		await socket.SendOperationAsync(_networkSerializationService, operation, linked.Token);
	}

	public EventReplicationService(
		  IOptions<EventReplicationConfig> options
		, IEventLog storage
		, EventReplicationState eventReplicationState
		, INetworkSerializationService networkSerializationService
		, JsonSerializerContext? jsonSerializerContext = null
		, EventReplicationConfig? config = null
		, IEventReplicationStateStore? stateStore = null
		, ISbxSerializerFactory? serializerFactory = null
		, StreamSnapshotFolding? snapshotFolding = null
		)
	{
		_storage = storage;
		_eventReplicationState = eventReplicationState;
		_networkSerializationService = networkSerializationService;
		_jsonSerializerContext = jsonSerializerContext;
		_config = config ?? options.Value;
		_stateStore = stateStore;
		_serializerFactory = serializerFactory;
		_foldsSnapshots = snapshotFolding is not null;
	}

	/// <summary>The stream this connection serves; the persisted state is keyed by it.</summary>
	Guid StreamKey => _config.StreamId ?? _config.InitialSubscribeStreamId ?? default;

	/// <summary>
	/// The stream a received event belongs to. The stream is not on the wire: the event's own when a peer set it, else the one
	/// stream this connection is subscribed to, else the configured one.
	/// </summary>
	Guid StreamOf(Event ev)
	{
		var stream = FindStreamOf(ev);
		if (stream == default)
		{
			throw new InvalidOperationException($"EventReplicationService: event {ev.EventId} arrived on a connection subscribed to {_activeStreams.Count} streams and no configured stream; its stream cannot be told.");
		}
		return stream;
	}

	/// <summary>The event's stream as <see cref="StreamOf"/> tells it, or default when it cannot be told.</summary>
	Guid FindStreamOf(Event ev)
	{
		var active = _activeStreams;
		return ev.StreamId != default ? ev.StreamId
			: active.Count == 1 ? active.First()
			: StreamKey;
	}

	/// <summary>
	/// The cursors of the last run describe the local log they were saved with: with that log empty (cleared, or a new
	/// store) they would resume past history this client no longer holds, so only the node id is kept.
	/// </summary>
	async Task LoadStateAsync(bool logHoldsEvents)
	{
		if (_stateStore is not null)
		{
			var saved = await _stateStore.LoadAsync(StreamKey, _cts.Token);
			if (saved is not null)
			{
				_eventReplicationState.MyNodeId = saved.MyNodeId;
				if (logHoldsEvents)
				{
					_eventReplicationState.LastEventIdFromMe = saved.LastEventIdFromMe;
					_eventReplicationState.ServerWatermark = saved.ServerWatermark;
				}
			}
		}
	}

	// Set by a Force Resync: this run has ended, and nothing it receives or sends is stored any more.
	volatile bool _ended;

	async Task SaveStateAsync()
	{
		if (_stateStore is not null && !_ended)
		{
			await _stateStore.SaveAsync(StreamKey, _eventReplicationState, _cts.Token);
		}
	}

	HashSet<Guid> _skipSet = new HashSet<Guid>();
	LinkedList<Guid> _skipList = new LinkedList<Guid>();

	static async Task<byte[]?> ReceiveFullMessageAsync(WebSocket ws, CancellationToken ct)
	{
		var rent = ArrayPool<byte>.Shared.Rent(DefaultFrameSize);
		try
		{
			using var ms = new MemoryStream(DefaultFrameSize);
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

	protected async override Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// A host stop ends the service: it cancels every socket read and wakes the send loop, which then sees the cancellation.
		using var stopping = stoppingToken.Register(() =>
		{
			_cts.Cancel();
			Wake();
		});

		// Seed the by-EventId dedup (_skipSet/_skipList, otherwise only populated as events are
		// received live) from whatever's already durably stored locally. Without this, the server's
		// backlog replay on reconnect (see SynqraReplicationEndpointExtensions.cs) would be echoed
		// straight back to it by the outgoing loop below. This service is transport-only: it never
		// touches a projection — the projection owner brings its stream up to date via the keeper on
		// the EventsReceived signal (and on first use through IProjectionProvider.GetAsync).
		//
		// A corrupt/undeserializable local record here means this client's local durable log can no
		// longer be trusted at all — clear it and continue with an empty skip-set and a cold
		// LastEventIdFromServer, which is a correct and safe "send me everything" signal, not a bug.
		try
		{
			// The ids alone: a log that indexes them (IndexedDB's tags) is not read at all. Reading and deserializing every
			// event here, beside the projection's own cold fold, held every click for seconds on a large stream (WI 487).
			var ids = _storage is IEventIdIndex index
				? index.EnumerateEventIdsAsync(_cts.Token)
				: _storage.GetAllAsync(from: default, _cts.Token).Select(ev => ev.EventId);
			await foreach (var id in ids)
			{
				lock (_skipSet)
				{
					if (_skipSet.Add(id))
					{
						_skipList.AddLast(id);
					}
				}
			}
		}
		catch (Exception ex)
		{
			EmergencyLog.Default.LogWarning(ex, "EventReplicationService: local durable log failed to replay — clearing it and resyncing fresh from the server.");
			await WipeLocalAsync();
		}

		bool logHoldsEvents;
		lock (_skipSet)
		{
			logHoldsEvents = _skipSet.Count > 0;
		}
		await LoadStateAsync(logHoldsEvents); // the cursor of the last run: HELLO asks for the delta, not the stream
		if (logHoldsEvents)
		{
			await ResumeAfterStoredSnapshotAsync();
		}
		_lastSentEventId = _eventReplicationState.LastEventIdFromMe; // this run offers everything the master has not acknowledged

		// One pass per connection. A reader that fails or a socket that closes ends the pass; the next one resumes from the
		// persisted cursor and offers again what the master has not acknowledged. The service stops only with the host.
		for (var failures = 0; !_cts.IsCancellationRequested; )
		{
			var (wsConnection, cut) = await ConnectAsync();
			using var abortOnCut = cut.Token.Register(wsConnection.Abort); // a cut connection ends at once: its reader and sends fail, the pass ends
			EmergencyLog.Default.Stage($"replication: connected (cursor {_eventReplicationState.ServerWatermark}, log ids {_skipList.Count})");
			var connectedAt = Stopwatch.StartNew();
			_lastSentEventId = _eventReplicationState.LastEventIdFromMe;
			_lastSentSequence = default;
			ConnectionFrames = 0;
			_catchUpRequest = Guid.CreateVersion7();
			_snapshotHeader = null; // a snapshot cut short with its connection is asked for again by the next one
			_snapshotChunks.Clear();

			async Task Reader()
			{
				#region HELLO
				var magicBytes = await ReceiveWatchedAsync(wsConnection);
				if (magicBytes == null || magicBytes.Length == 0)
				{
					IsOnline = false;
					return;
				}
				if (magicBytes.Length != 8)
				{
					var sb = new System.Text.StringBuilder();
					new HexDumpWriter().HexDump(magicBytes, s => sb.Append(s), c => sb.Append(c));
					throw new Exception($"Protocol Negotiation Failed! Received {magicBytes.Length} bytes instead of 8. {Environment.NewLine}{sb}");
				}
				var magic = BitConverter.ToUInt64(magicBytes);
				if (magic != _networkSerializationService.Magic)
				{
					throw new Exception($"Protocol Negotiation Failed! Received Magic {magic:X16} instead of {_networkSerializationService.Magic:X16}.");
				}
				IsOnline = true; // the master registers the socket before answering HELLO, so from here on broadcasts include this node
				EmergencyLog.Default.Stage("replication: online");
				#endregion
				while (!_cts.IsCancellationRequested)
				{
					var bytes = await ReceiveWatchedAsync(wsConnection);
					if (bytes == null || bytes.Length == 0)
					{
						IsOnline = false;
						break;
					}
					var operation = _networkSerializationService.Deserialize<TransportOperation>(bytes);
					switch (operation)
					{
						case SubscriptionState state:
							// Master's authoritative snapshot of the streams this connection is now
							// subscribed to. Lets the client detect an unexpected default and drive its
							// own UI off the confirmed set. Sent after HELLO and every sub/unsub.
							_activeStreams = state.Streams as IReadOnlyCollection<Guid> ?? state.Streams.ToArray();
							ConfirmNamedStream(state);
							break;
						case EventEnvelope envelope:
							await ReceiveAsync(envelope);
							break;
						case EventAccepted accepted:
							// Acks arrive in the order the events were sent, so the latest one is the cursor.
							_eventReplicationState.LastEventIdFromMe = accepted.EventId;
							await SaveStateAsync();
							Settle(accepted.EventId, null);
							break;
						case SnapshotBegin begin:
							ReceiveSnapshotBegin(begin);
							break;
						case SnapshotChunk chunk:
							ReceiveSnapshotChunk(chunk);
							break;
						case SnapshotEnd end:
							await StoreSnapshotAsync(end);
							break;
						case CaughtUp caughtUp:
							ReceiveCaughtUp(caughtUp);
							break;
						case SnapshotDeclined declined:
							// Whatever part of a snapshot came is dropped: the backlog that follows is the whole stream.
							_snapshotHeader = null;
							_snapshotChunks.Clear();
							_awaitingSnapshot = false;
							EmergencyLog.Default.Stage($"replication: snapshot of {declined.StreamId} declined ({declined.Reason}); the backlog follows");
							break;
						case EventRejected rejected:
							// Refused for good: the cursor moves past it like on an ack, so it is not offered on every connect, and its local effect is undone.
							EmergencyLog.Default.LogWarning($"EventReplicationService: the master refused event {rejected.EventId}: {rejected.Reason}");
							_eventReplicationState.LastEventIdFromMe = rejected.EventId;
							await SaveStateAsync();
							await UndoAsync(rejected.EventId);
							Settle(rejected.EventId, rejected.Reason);
							break;
						default:
							EmergencyLog.Default.LogWarning($"EventReplicationService: ignoring a transport operation this build does not know: {operation?.GetType()}."); // a newer master; the stream goes on
							break;
					}
				}
			}
			var reader = ObserveAsync(Reader());
			// The send loop ends this connection once it sees the reader complete, so it is woken only after: a wake from inside the
			// reader came before the task completed, and the loop could find it still running, take another pass and wait for a wake
			// that never came (a cold client writes nothing), never reconnecting.
			_ = reader.ContinueWith(_ => Wake(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			_readerTask = reader;
			var watchdog = WatchCatchUpAsync(cut);

			// A known id (own, or resent as backlog) is not appended again: the local log keeps one record per event. The
			// resume point on the envelope is the cursor; it only ever moves forward, and it is persisted as it moves, after the
			// event is in the local log. The frame's own sequence caps it here as well: a master from before per-frame resume
			// points sends the tail on every backlog frame, and a run ending mid-replay would resume past what it never got.
			async Task ReceiveAsync(EventEnvelope envelope)
			{
				FramesReceived++;
				ConnectionFrames++;
				if (FramesReceived <= 3 || FramesReceived % 100 == 0)
				{
					EmergencyLog.Default.Stage($"replication: frame {FramesReceived} ({envelope.Event.GetType().Name} {envelope.Event.EventId})");
				}
				var ev = envelope.Event;
				var sequence = ev.SequenceId; // the master's stamp, read before the local log stamps its own
				var resumePoint = sequence != default && sequence.CompareBigEndian(envelope.Watermark) < 0 ? sequence : envelope.Watermark;
				// Covered by the snapshot the log holds for its stream: a live push of an event committed before the cut can arrive after
				// the newer backlog, and applying it then would put an older value back over a newer one.
				var covered = sequence != default
					&& _snapshotWatermarks.TryGetValue(FindStreamOf(ev), out var cutAt)
					&& sequence.CompareBigEndian(cutAt) <= 0;
				bool known;
				lock (_skipSet)
				{
					var added = _skipSet.Add(ev.EventId);
					if (added)
					{
						_skipList.AddLast(ev.EventId);
					}
					known = !added || covered;
					_heldByMaster.Add(ev.EventId);
				}
				if (!known && !_ended)
				{
					ev.StreamId = StreamOf(ev);
					await _storage.AppendAsync(ev); // the local log stamps its own arrival order over the server's sequence
					if (EmergencyLog.Default.IsEnabled(LogLevel.Information)) // the message is built per call; off, nothing is
					{
						EmergencyLog.Default.LogInformation($"{GetHashCode():X4} <<< {ev}");
					}
				}
				if (resumePoint.CompareBigEndian(_eventReplicationState.ServerWatermark) > 0)
				{
					_eventReplicationState.ServerWatermark = resumePoint;
					await SaveStateAsync();
				}
				if (!known)
				{
					EventsReceived?.Invoke(); // the owner folds the delta in through the keeper
				}
				// The send loop steps over it and saves the cursor past it in this run: the held set dies with the run, so a page
				// that wrote nothing sent everything it received back on its next load (pr1246: 5326 events, a 23 s warm load).
				Wake();
			}

			#region HELLO
			{
				// 8 bytes magic + 16 bytes ServerWatermark (Guid.Empty = "send me everything") + the
				// HELLO "ws-method": byte[24] kind, and for Hello_SubscribeTo the next 16 bytes are the
				// stream id (total 41 bytes). See SynqraReplicationEndpointExtensions.cs for the server side.
				var helloKind = ChooseHelloKind();
				_helloNamedStream = helloKind == ReplicationHelloKind.SubscribeToSnapshot && _config.HelloKind == ReplicationHelloKind.UserDefaultMainStream;
				_awaitingSnapshot = helloKind == ReplicationHelloKind.SubscribeToSnapshot;
				var isSubscribeTo = helloKind is ReplicationHelloKind.SubscribeTo or ReplicationHelloKind.SubscribeToSnapshot;
				var helloSize = isSubscribeTo ? 41 : 25;
				var buffer = ArrayPool<byte>.Shared.Rent(helloSize);
				try
				{
					BitConverter.TryWriteBytes(buffer, _networkSerializationService.Magic);
					_eventReplicationState.ServerWatermark.TryWriteBytes(buffer.AsSpan(8, 16));
					buffer[24] = (byte)helloKind;
					if (isSubscribeTo)
					{
						(_config.InitialSubscribeStreamId ?? (StreamKey != default ? StreamKey : throw new InvalidOperationException("HelloKind SubscribeTo requires InitialSubscribeStreamId."))).TryWriteBytes(buffer.AsSpan(25, 16));
					}
					await wsConnection.SendAsync(new ArraySegment<byte>(buffer, 0, helloSize), WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
					// Read by the master after the catch-up, so its answer marks the end of it. Sent before the send loop starts: a socket takes one send at a time.
					await wsConnection.SendOperationAsync(_networkSerializationService, new CatchUpRequest { RequestId = _catchUpRequest }, _cts.Token);
				}
				catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException && cut.IsCancellationRequested && !_cts.IsCancellationRequested)
				{
					// The socket broke (or was cut) before the master heard us: the reader sees it too and ends this connection.
					EmergencyLog.Default.Log(_cutBy is null ? LogLevel.Error : LogLevel.Information, ex, $"EventReplicationService: the HELLO could not be sent{CutNote}; reconnecting.");
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(buffer);
				}
			}
			#endregion
			EmergencyLog.Default.Stage($"replication: HELLO sent ({ChooseHelloKind()})");
			Wake(); // whatever the last run left unacknowledged goes now, not at the next local write

			try
			{
				while (!reader.IsCompleted)
				{
					await _autoResetEvent.WaitAsync();
					if (EmergencyLog.Default.IsEnabled(LogLevel.Information)) // the message is built per call; off, nothing is
					{
						EmergencyLog.Default.LogInformation($"{GetHashCode():X4} >>> <TRIGGERED LOOP>");
					}
					if (_cts.IsCancellationRequested || reader.IsCompleted)
					{
						break;
					}
					// get new events from storage and send them to server
					// A fresh read per trigger: a storage's enumeration ends once it runs dry (IndexedDb stops at an empty
					// page), so one enumerator kept across triggers sent nothing after the first drain. A full read, skipping
					// up to the last sent id, as the keeper does: `from` is not a uniform range bound across backends.
					// The first read of a run is a full one, walking to the last acknowledged id; once this run knows where that is in
					// the log, a trigger reads only what follows, as the keeper does (a full read per local write cost seconds, WI 487).
					var sent = _lastSentEventId;
					var sendingStream = StreamKey != default ? StreamKey : _lastSentStream; // a client's log holds the one stream it serves
					var incremental = _lastSentSequence != default && sendingStream != default;
					var reached = sent == default || incremental;
					var pending = incremental
						? _storage.ReadStreamAsync(sendingStream, _lastSentSequence, _cts.Token)
						: _storage.GetAllAsync(from: default, _cts.Token);
					await foreach (var ev in pending)
					{
						if (!reached)
						{
							reached = ev.EventId == sent;
							if (reached)
							{
								Passed(ev);
							}
							continue;
						}
						bool held;
						lock (_skipSet)
						{
							held = _heldByMaster.Contains(ev.EventId) || ev is StreamSnapshotEvent || ev.IsLocalUndo; // a snapshot is this client's own record of what the master sent; an undo takes back what the master refused
						}
						if (held)
						{
							// The master sent it, so it has it: stepped over, never echoed. With nothing of ours awaiting an ack the
							// persisted cursor moves too, so the next run does not walk (or send) it again.
							if (_lastSentEventId == _eventReplicationState.LastEventIdFromMe)
							{
								_eventReplicationState.LastEventIdFromMe = ev.EventId;
							}
							_lastSentEventId = ev.EventId;
							Passed(ev);
							continue;
						}
						// Single-stream connection: skip events belonging to any other stream that shares this
						// local multitenant store (e.g. the primary stream's events when this connection only
						// replicates a "/tracking" sub-stream). The outbound cursor still advances past them so
						// they are never re-examined, and they are never sent to — and mis-stamped by — the
						// stream-scoped master endpoint. StreamId is set locally even though it is off the wire.
						if (_config.StreamId is Guid onlyStream && ev.StreamId != onlyStream)
						{
							_lastSentEventId = ev.EventId;
							Passed(ev);
							continue;
						}
						lock (_skipSet)
						{
							if (_skipSet.Add(ev.EventId))
							{
								_skipList.AddLast(ev.EventId);
							}
						}
						// await _connection.InvokeAsync("EventEnvelope", ev);

						var inv = new EventEnvelope() { Event = ev };

						var pool = ArrayPool<byte>.Shared;
						var bytes = pool.Rent(DefaultFrameSize);
						try
						{
							if (EmergencyLog.Default.IsEnabled(LogLevel.Information)) // the message is built per call; off, nothing is
							{
								EmergencyLog.Default.LogInformation($"{GetHashCode():X4} >>> {ev}");
							}
							await wsConnection.SendOperationAsync(_networkSerializationService, inv, bytes, _cts.Token);
						}
						finally
						{
							pool.Return(bytes);
						}

						FramesSent++;
						lock (_skipSet)
						{
							_awaitingVerdict[ev.EventId] = ev.CommandId;
						}
						_lastSentEventId = ev.EventId; // written, not yet acknowledged: the master's EventAccepted moves the persisted cursor
						Passed(ev);
					}
					if (!reached)
					{
						// The cursor names an event this log does not hold (a log cleared or rebuilt under a kept state): walking for
						// it would never send again. Start over from the top; what the master already holds is stepped over above.
						EmergencyLog.Default.LogWarning($"EventReplicationService: outbound cursor {sent} is not in the local log; offering the log from its start.");
						_lastSentEventId = default;
						_lastSentSequence = default;
						Wake();
					}
					await SaveStateAsync();
					if (EmergencyLog.Default.IsEnabled(LogLevel.Information)) // the message is built per call; off, nothing is
					{
						EmergencyLog.Default.LogInformation($"{GetHashCode():X4} >>> </LOOP>");
					}

					// get events from server and apply them locally
				}
			}
			catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException && cut.IsCancellationRequested && !_cts.IsCancellationRequested)
			{
				// The socket broke (or was cut) under a send: this pass is over, the next connection sends it again.
				EmergencyLog.Default.Log(_cutBy is null ? LogLevel.Error : LogLevel.Information, ex, $"EventReplicationService: a send failed{CutNote}; reconnecting.");
			}
			IsOnline = false;
			cut.Cancel(); // ends the watchdog
			wsConnection.Abort();
			wsConnection.Dispose();
			await reader; // it logged its own failure
			await watchdog;
			if (_snapshotHeader is not null && _cutBy != nameof(ReconnectNow) && !_cutCounted && ++_snapshotsCutShort >= 2)
			{
				_snapshotRefused = true; // the second connection lost mid-snapshot: this run replays; a user's reconnect is no such pattern
			}
			CloseAttempt(cut);
			failures = connectedAt.Elapsed > TimeSpan.FromSeconds(30) ? 0 : failures + 1; // a connection that held a while was not the problem
			if (!_cts.IsCancellationRequested)
			{
				await PauseAsync(CalculateBackoff(failures));
			}
		}
	}

	/*
	public async Task StopAsync(CancellationToken cancellationToken)
	{
		await _cts.CancelAsync();
		_autoResetEvent.Set();
		// _ = _connection?.StopAsync();
		if (_connection != null && _connection.State == WebSocketState.Open)
		{
			await _connection.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
		}
		if (_readerTask != null)
		{
			await _readerTask.WaitAsync(cancellationToken);
		}
		if (_writerTask != null)
		{
			await _writerTask.WaitAsync(cancellationToken);
		}
		// return Task.CompletedTask;
	}
	*/

	public void Trigger(Command command, IReadOnlyList<Event> events) => Wake();

	/// <summary>
	/// The HELLO kind this connection announces: the configured one, or a snapshot request when this client is fully cold (no
	/// watermark, nothing in its log), knows its stream, has a projection that folds one (<see cref="StreamSnapshotFolding"/>), and
	/// has not given up on snapshots this run.
	/// </summary>
	ReplicationHelloKind ChooseHelloKind()
	{
		bool logEmpty;
		lock (_skipSet)
		{
			logEmpty = _skipSet.Count == 0;
		}
		var cold = logEmpty && _eventReplicationState.ServerWatermark == default;
		return cold
			&& _config.RequestSnapshot
			&& _foldsSnapshots
			&& !_snapshotRefused
			&& StreamKey != default
			&& _config.HelloKind is ReplicationHelloKind.UserDefaultMainStream or ReplicationHelloKind.SubscribeTo
			? ReplicationHelloKind.SubscribeToSnapshot
			: _config.HelloKind;
	}

	/// <summary>
	/// A log that starts with a snapshot holds the stream's state up to its watermark and none of the events before it, so the
	/// watermark is the floor of this client's resume point, and an event stamped at or before it is never applied again. A host
	/// that keeps no replication state (or lost it) would otherwise be sent the whole history and fold it over the snapshot.
	/// </summary>
	async Task ResumeAfterStoredSnapshotAsync()
	{
		var first = StreamKey != default
			? _storage.ReadStreamAsync(StreamKey, default, _cts.Token)
			: _storage.GetAllAsync(from: default, _cts.Token);
		await foreach (var ev in first)
		{
			if (ev is StreamSnapshotEvent snapshot)
			{
				_snapshotWatermarks[snapshot.Header.StreamId] = snapshot.Header.Watermark;
				if (snapshot.Header.Watermark.CompareBigEndian(_eventReplicationState.ServerWatermark) > 0)
				{
					_eventReplicationState.ServerWatermark = snapshot.Header.Watermark;
				}
			}
			break; // a snapshot is only ever the first record
		}
	}

	/// <summary>
	/// A HELLO that asked for a snapshot in place of UserDefaultMainStream named the stream itself; the master subscribes it only
	/// when that stream is one the connection may read. When it took another (or none), the snapshot request changed what this
	/// client gets: it reconnects with the configured kind and replays, as it did before snapshots.
	/// </summary>
	void ConfirmNamedStream(SubscriptionState state)
	{
		if (_helloNamedStream)
		{
			_helloNamedStream = false;
			if (!state.Streams.Contains(StreamKey))
			{
				_snapshotRefused = true;
				throw new InvalidOperationException($"EventReplicationService: the master did not subscribe this connection to stream {StreamKey}, which its snapshot request named; reconnecting with {_config.HelloKind}.");
			}
		}
	}

	/// <summary>
	/// A snapshot starts. Its header is checked at once: a schema this build cannot read would be stored and then refused on every
	/// load, so the connection ends instead and the next one replays the stream.
	/// </summary>
	void ReceiveSnapshotBegin(SnapshotBegin begin)
	{
		if (_serializerFactory is not null)
		{
			try
			{
				new SnapshotSchema(_serializerFactory).CreateReader(begin);
			}
			catch (SnapshotSchemaRefusedException refused)
			{
				_snapshotRefused = true;
				throw new InvalidOperationException($"EventReplicationService: snapshot {begin.SnapshotId} of stream {begin.StreamId} cannot be read by this build ({refused.Message}); reconnecting to replay the stream.", refused);
			}
		}
		_snapshotHeader = begin;
		_snapshotChunks.Clear();
		EmergencyLog.Default.Stage($"replication: snapshot {begin.SnapshotId} of {begin.StreamId} at {begin.Watermark} begins");
	}

	void ReceiveSnapshotChunk(SnapshotChunk chunk)
	{
		if (_snapshotHeader?.SnapshotId != chunk.SnapshotId || chunk.Index != _snapshotChunks.Count)
		{
			_snapshotRefused = true; // a broken snapshot is broken again on the next connection: this run replays
			throw new InvalidOperationException($"EventReplicationService: snapshot chunk {chunk.SnapshotId} #{chunk.Index} arrived out of place (expected #{_snapshotChunks.Count} of {_snapshotHeader?.SnapshotId}); reconnecting.");
		}
		_snapshotChunks.Add(chunk);
	}

	/// <summary>
	/// The snapshot is complete: kept as the first record of the local log, then its watermark becomes the resume point, so a
	/// load never resumes past a state it does not hold. One that came incomplete ends the connection; the next replays.
	/// </summary>
	async Task StoreSnapshotAsync(SnapshotEnd end)
	{
		var header = _snapshotHeader;
		var records = _snapshotChunks.Sum(x => x.Count);
		if (header is null || header.SnapshotId != end.SnapshotId || _snapshotChunks.Count != end.ChunkCount || records != end.RecordCount)
		{
			_snapshotRefused = true; // a broken snapshot is broken again on the next connection: this run replays
			throw new InvalidOperationException($"EventReplicationService: snapshot {end.SnapshotId} ended with {_snapshotChunks.Count} of {end.ChunkCount} chunks and {records} of {end.RecordCount} records; reconnecting.");
		}
		var snapshot = new StreamSnapshotEvent
		{
			EventId = end.SnapshotId,
			CommandId = end.SnapshotId,
			StreamId = header.StreamId,
			Header = header,
			Chunks = _snapshotChunks.ToList(),
		};
		_snapshotHeader = null;
		_snapshotChunks.Clear();
		lock (_skipSet)
		{
			if (_skipSet.Add(snapshot.EventId))
			{
				_skipList.AddLast(snapshot.EventId);
			}
			_heldByMaster.Add(snapshot.EventId);
		}
		if (!_ended)
		{
			await _storage.AppendAsync(snapshot);
		}
		if (end.Watermark.CompareBigEndian(_eventReplicationState.ServerWatermark) > 0)
		{
			_eventReplicationState.ServerWatermark = end.Watermark;
			await SaveStateAsync();
		}
		_snapshotWatermarks[header.StreamId] = end.Watermark;
		_awaitingSnapshot = false;
		_snapshotsCutShort = 0; // a snapshot came in: the cuts before it were no pattern
		SnapshotsLoaded++;
		EmergencyLog.Default.Stage($"replication: snapshot {end.SnapshotId} stored, {end.RecordCount} records in {end.ChunkCount} chunks; the backlog after {end.Watermark} follows");
		EventsReceived?.Invoke(); // the owner folds it in through the keeper
		Wake();
	}

	/// <summary>The master has sent what it held for this connection: what follows is live. An answer to an earlier connection's probe is not.</summary>
	void ReceiveCaughtUp(CaughtUp caughtUp)
	{
		if (caughtUp.RequestId == _catchUpRequest && !_isCaughtUp)
		{
			lock (_reconnectGate)
			{
				_reconnectAtOnce = false; // a ReconnectNow is answered: a later disconnect waits its pause as usual
			}
			_isCaughtUp = true;
			EmergencyLog.Default.Stage($"replication: caught up after {ConnectionFrames} frames");
			StatusChanged?.Invoke();
		}
	}

	/// <summary>
	/// Takes back, in the local log, an own event the master refused: the undo is written after it as a local undo (never sent)
	/// and folded in by the keeper like a received event. The whole log is read: a refusal is rare, and the undo must see every
	/// event of the master's that already came for the same state.
	/// </summary>
	async Task UndoAsync(Guid refusedId)
	{
		var log = new List<Event>();
		await foreach (var ev in _storage.GetAllAsync(from: default, _cts.Token))
		{
			log.Add(ev);
		}
		var undo = _refused.Undo(log, refusedId);
		if (undo.Count > 0 && !_ended)
		{
			foreach (var ev in undo)
			{
				lock (_skipSet)
				{
					if (_skipSet.Add(ev.EventId))
					{
						_skipList.AddLast(ev.EventId);
					}
				}
				await _storage.AppendAsync(ev);
			}
			EmergencyLog.Default.LogWarning($"EventReplicationService: {undo.Count} local undo event(s) take back refused event {refusedId}.");
			EventsReceived?.Invoke(); // the owner folds the undo in through the keeper
		}
	}

	/// <summary>Tells listeners the master's answer to an own event. A listener's failure is logged, never the reader's end.</summary>
	void Settle(Guid eventId, string? refusal)
	{
		Guid commandId;
		lock (_skipSet)
		{
			_awaitingVerdict.Remove(eventId, out commandId);
		}
		try
		{
			EventSettled?.Invoke(new EventVerdict(eventId, commandId, refusal));
		}
		catch (Exception ex)
		{
			EmergencyLog.Default.LogError(ex, $"EventReplicationService: a listener failed on the verdict for event {eventId}; replication goes on.");
		}
	}

	/// <summary>Remembers where the send loop is in the local log, so its next read starts there.</summary>
	void Passed(Event ev)
	{
		_lastSentSequence = ev.SequenceId;
		_lastSentStream = ev.StreamId;
	}

	/// <summary>Wakes the send loop.</summary>
	void Wake()
	{
		// Already signaled is the common case (one trigger per command); a thrown-and-caught exception for each
		// is costly on the browser, so the signal is checked first and the catch covers only a concurrent race.
		if (_autoResetEvent.CurrentCount == 0)
		{
			try
			{
				_autoResetEvent.Release();
			}
			catch (SemaphoreFullException)
			{
				// a concurrent trigger signaled first; the loop runs once for both
			}
		}
	}

	/// <summary>
	/// Opens the socket, retrying with a growing pause while the master cannot be reached; only the host's stop ends the wait.
	/// Returns it with the attempt that opened it: cancelling that cuts the connection.
	/// </summary>
	async Task<(ClientWebSocket Socket, CancellationTokenSource Cut)> ConnectAsync()
	{
		(ClientWebSocket Socket, CancellationTokenSource Cut)? connected = null;
		for (var attempt = 0; connected is null; attempt++)
		{
			var socket = new ClientWebSocket();
			_config.ConfigureSocket?.Invoke(socket.Options);
			var cut = OpenAttempt();
			try
			{
				await socket.ConnectAsync(_config.ResolveEndpointUri(), cut.Token);
				_wsConnection = socket;
				_networkSerializationService.Reinitialize();
				// Not online yet: that is declared only after the HELLO handshake completes,
				// when the master is guaranteed to have registered this node for broadcasts.
				connected = (socket, cut);
			}
			catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException || ex is OperationCanceledException && !_cts.IsCancellationRequested)
			{
				socket.Dispose();
				CloseAttempt(cut);
				EmergencyLog.Default.Log(_cutBy is null ? LogLevel.Warning : LogLevel.Information, ex, $"EventReplicationService: connect attempt {attempt} failed{CutNote}: {ex.Message}");
				await PauseAsync(CalculateBackoff(attempt));
				_cts.Token.ThrowIfCancellationRequested(); // the host's stop ends the service here, as it always did
			}
		}
		return connected.Value;
	}

	/// <summary>Starts a connect attempt that <see cref="ReconnectNow"/> can cut short, and its connection's watchdog clock.</summary>
	CancellationTokenSource OpenAttempt()
	{
		var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
		_cutBy = null;
		_cutCounted = false;
		Volatile.Write(ref _waitingSince, Stopwatch.GetTimestamp());
		lock (_reconnectGate)
		{
			_attempt = attempt;
		}
		return attempt;
	}

	/// <summary>Ends an attempt or its connection: out of ReconnectNow's reach first, then disposed.</summary>
	void CloseAttempt(CancellationTokenSource attempt)
	{
		lock (_reconnectGate)
		{
			if (_attempt == attempt)
			{
				_attempt = null;
			}
		}
		attempt.Dispose();
	}

	/// <summary>" (cut by the …)" when a ReconnectNow or the watchdog cut this connection: its failures are then expected.</summary>
	string CutNote => _cutBy is { } by ? $" (cut by the {by})" : "";

	/// <summary>How long the reader has waited for the frame it waits for; zero while it handles one.</summary>
	TimeSpan Silence
	{
		get
		{
			var since = Volatile.Read(ref _waitingSince);
			return since == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(since);
		}
	}

	/// <summary>Cuts a connect attempt or a connection short, naming who did, so its end reads as expected.</summary>
	void CutConnection(CancellationTokenSource attempt, string by)
	{
		_cutBy ??= by;
		CancelInBackground(attempt);
	}

	/// <summary>Cancels without running the callbacks (a socket's abort, a pause's end) on this thread, which may hold the gate; a callback's failure is logged.</summary>
	void CancelInBackground(CancellationTokenSource source)
	{
		source.CancelAsync().ContinueWith(
			  t => EmergencyLog.Default.LogError(t.Exception, "EventReplicationService: a cancellation callback failed.")
			, CancellationToken.None
			, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously
			, TaskScheduler.Default
			);
	}

	/// <inheritdoc />
	/// <remarks>
	/// Acts only while the connection looks dead: during a pause, or not caught up with nothing heard for two seconds, counted from
	/// the attempt's start until the HELLO reply and from the last frame after it. A master still seeding or cutting a snapshot
	/// before its HELLO, and a catch-up still receiving, are left alone, as a caught-up connection is: a host calls this on every
	/// tab return and network change.
	/// </remarks>
	public void ReconnectNow()
	{
		// Cancelled under the gate: CloseAttempt takes the gate before it disposes, so nothing cancelled here is disposed yet.
		lock (_reconnectGate)
		{
			var dead = _pause is not null || !IsCaughtUp && Silence >= TimeSpan.FromSeconds(2);
			if (dead)
			{
				EmergencyLog.Default.Stage($"replication: reconnect requested (online: {IsOnline}, frames {ConnectionFrames}, silent {Silence.TotalSeconds:F1} s)");
				_reconnectAtOnce = true;
				if (_pause is { } pause)
				{
					CancelInBackground(pause);
				}
				if (_attempt is { } attempt)
				{
					CutConnection(attempt, nameof(ReconnectNow));
				}
			}
		}
	}

	/// <summary>Waits before the next connect attempt, announcing when it is due; a <see cref="ReconnectNow"/> or the host's stop ends the wait.</summary>
	async Task PauseAsync(TimeSpan delay)
	{
		using var pause = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
		bool atOnce;
		lock (_reconnectGate)
		{
			atOnce = _reconnectAtOnce;
			_reconnectAtOnce = false;
			if (!atOnce)
			{
				_pause = pause;
			}
		}
		if (atOnce)
		{
			EmergencyLog.Default.Stage("replication: reconnecting at once");
		}
		else
		{
			AnnounceNextAttempt(DateTimeOffset.UtcNow + delay);
			await Task.WhenAny(Task.Delay(delay, pause.Token)); // a cut-short pause ends without an exception
			lock (_reconnectGate)
			{
				_pause = null;
				_reconnectAtOnce = false; // the request that cut this pause short is answered by the attempt that follows
			}
			AnnounceNextAttempt(null);
		}
	}

	void AnnounceNextAttempt(DateTimeOffset? at)
	{
		lock (_reconnectGate)
		{
			_nextAttemptAt = at;
		}
		StatusChanged?.Invoke();
	}

	/// <summary>Receives the next frame, telling the catch-up watchdog how long this connection has been waiting for it.</summary>
	async Task<byte[]?> ReceiveWatchedAsync(WebSocket ws)
	{
		Volatile.Write(ref _waitingSince, Stopwatch.GetTimestamp());
		var bytes = await ReceiveFullMessageAsync(ws, _cts.Token);
		Volatile.Write(ref _waitingSince, 0); // handling a frame is not silence: a slow local store never cuts the connection
		return bytes;
	}

	/// <summary>
	/// Until the master answers this connection's catch-up probe, a silence of <see cref="EventReplicationConfig.CatchUpTimeout"/>
	/// cuts the connection, and the next one resumes from the watermark: a master stuck in its backlog read kept a client
	/// "syncing" forever (prod: 606 frames, then nothing, and no CaughtUp).
	/// </summary>
	async Task WatchCatchUpAsync(CancellationTokenSource cut)
	{
		var timeout = _config.CatchUpTimeout;
		var watching = timeout > TimeSpan.Zero;
		while (watching && !cut.IsCancellationRequested && !_isCaughtUp)
		{
			var silence = Silence;
			if (silence >= timeout)
			{
				EmergencyLog.Default.LogError($"EventReplicationService: no frame for {silence.TotalSeconds:F0} s and no CaughtUp after {ConnectionFrames} frames (online: {IsOnline}); the connection is taken for dead and opened again from watermark {ServerWatermark}.");
				_cutCounted = _awaitingSnapshot && _snapshotHeader is null; // counted here: the end of the pass does not count it again
				if (_cutCounted && ++_snapshotsCutShort >= 2)
				{
					_snapshotRefused = true; // a snapshot the master cannot cut in time would cut every connection: this run replays
				}
				CutConnection(cut, "catch-up watchdog");
				watching = false;
			}
			else
			{
				var wait = timeout - silence;
				await Task.WhenAny(Task.Delay(wait < TimeSpan.FromHours(1) ? wait : TimeSpan.FromHours(1), cut.Token)); // the connection's end ends the wait
			}
		}
	}

	/// <summary>
	/// Waits for a connection's reader. Whatever ends it ends the connection, which the service then opens again: its exception
	/// was left in an unobserved task, so replication stalled with no trace and IsOnline stayed true (dev 2026-10-23).
	/// </summary>
	async Task ObserveAsync(Task reader)
	{
		try
		{
			await reader;
		}
		catch (OperationCanceledException ex) when (_cts.IsCancellationRequested)
		{
			EmergencyLog.Default.LogDebug(ex, "EventReplicationService: the reader stopped with the service.");
		}
		catch (Exception ex) when (_cutBy is { } by && ex is WebSocketException or OperationCanceledException)
		{
			EmergencyLog.Default.LogInformation(ex, $"EventReplicationService: the reader stopped as the connection was cut by the {by}; reconnecting.");
		}
		catch (Exception ex)
		{
			// Tolerable: the next connection resumes from the persisted cursor. A frame that fails every time shows as repeated errors.
			EmergencyLog.Default.LogError(ex, "EventReplicationService: the reader stopped on an exception; reconnecting.");
		}
		finally
		{
			IsOnline = false;
			EmergencyLog.Default.Stage($"replication: reader ended after {FramesReceived} frames");
		}
	}

	/// <summary>
	/// The pause before connecting again: half a second, doubling with each failure in a row, at most eight seconds, each within 20 %
	/// below or above (at the cap only below it), so the clients a server restart dropped together do not retry in lockstep.
	/// </summary>
	internal TimeSpan CalculateBackoff(int failures)
	{
		var middle = Math.Min(8_000, 500 * (1 << Math.Min(failures, 6)));
		var spread = middle < 8_000 ? 0.4 : 0.2; // at the cap the spread stays, below it
		return TimeSpan.FromMilliseconds(Math.Min(8_000, middle * (0.8 + spread * Random.Shared.NextDouble())));
	}

	/// <inheritdoc />
	/// <remarks>
	/// The run ends first: a frame received or a send loop pass after the wipe persisted a server watermark again, so the
	/// reload resumed near the tail instead of replaying the stream, and a hole in the log was never filled (WI 487).
	/// </remarks>
	public async Task ClearLocalStorageAsync()
	{
		_ended = true;
		_cts.Cancel();
		Wake();
		await WipeLocalAsync();
		if (_stateStore is not null)
		{
			await _stateStore.SaveAsync(StreamKey, _eventReplicationState, CancellationToken.None); // the cursors of an empty log
		}
	}

	/// <summary>Empties the local log, the dedup and the cursors in memory; the caller decides whether the run goes on.</summary>
	async Task WipeLocalAsync()
	{
		lock (_skipSet)
		{
			_skipSet.Clear();
			_skipList.Clear();
		}
		_eventReplicationState.ServerWatermark = default;
		_eventReplicationState.LastEventIdFromMe = default;
		lock (_skipSet)
		{
			_heldByMaster.Clear();
			_awaitingVerdict.Clear();
		}
		_lastSentEventId = default;
		_lastSentSequence = default;
		_lastSentStream = default;
		if (_storage is IClearableAppendStorage clearable)
		{
			await clearable.ClearAllAsync();
		}
		await SaveStateAsync();
	}
}
