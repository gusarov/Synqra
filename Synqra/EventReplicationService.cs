using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage;
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

	private SemaphoreSlim _autoResetEvent = new SemaphoreSlim(0, 1);
	private CancellationTokenSource _cts = new CancellationTokenSource();

	volatile bool _isOnline;
	public bool IsOnline { get => _isOnline; private set => _isOnline = value; }

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

	// Ids the master sent this run: it holds them, so the send loop steps over them instead of echoing them back.
	readonly HashSet<Guid> _heldByMaster = new HashSet<Guid>();

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
		)
	{
		_storage = storage;
		_eventReplicationState = eventReplicationState;
		_networkSerializationService = networkSerializationService;
		_jsonSerializerContext = jsonSerializerContext;
		_config = config ?? options.Value;
		_stateStore = stateStore;
	}

	/// <summary>The stream this connection serves; the persisted state is keyed by it.</summary>
	Guid StreamKey => _config.StreamId ?? _config.InitialSubscribeStreamId ?? default;

	/// <summary>
	/// The stream a received event belongs to. The stream is not on the wire: the event's own when a peer set it, else the one
	/// stream this connection is subscribed to, else the configured one.
	/// </summary>
	Guid StreamOf(Event ev)
	{
		var active = _activeStreams;
		var stream = ev.StreamId != default ? ev.StreamId
			: active.Count == 1 ? active.First()
			: StreamKey;
		if (stream == default)
		{
			throw new InvalidOperationException($"EventReplicationService: event {ev.EventId} arrived on a connection subscribed to {active.Count} streams and no configured stream; its stream cannot be told.");
		}
		return stream;
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
		_lastSentEventId = _eventReplicationState.LastEventIdFromMe; // this run offers everything the master has not acknowledged

		// One pass per connection. A reader that fails or a socket that closes ends the pass; the next one resumes from the
		// persisted cursor and offers again what the master has not acknowledged. The service stops only with the host.
		for (var failures = 0; !_cts.IsCancellationRequested; )
		{
			var wsConnection = await ConnectAsync();
			EmergencyLog.Default.Stage($"replication: connected (cursor {_eventReplicationState.ServerWatermark}, log ids {_skipList.Count})");
			var connectedAt = Stopwatch.StartNew();
			_lastSentEventId = _eventReplicationState.LastEventIdFromMe;
			_lastSentSequence = default;

			async Task Reader()
			{
				#region HELLO
				var magicBytes = await ReceiveFullMessageAsync(wsConnection, _cts.Token);
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
					var bytes = await ReceiveFullMessageAsync(wsConnection, _cts.Token);
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
							break;
						case EventEnvelope envelope:
							await ReceiveAsync(envelope);
							break;
						case EventAccepted accepted:
							// Acks arrive in the order the events were sent, so the latest one is the cursor.
							_eventReplicationState.LastEventIdFromMe = accepted.EventId;
							await SaveStateAsync();
							break;
						case EventRejected rejected:
							// Refused for good: the cursor moves past it like on an ack, so it is not offered on every connect; its local effect stays until the master's events for the same state arrive.
							EmergencyLog.Default.LogWarning($"EventReplicationService: the master refused event {rejected.EventId}: {rejected.Reason}");
							_eventReplicationState.LastEventIdFromMe = rejected.EventId;
							await SaveStateAsync();
							break;
						default:
							EmergencyLog.Default.LogWarning($"EventReplicationService: ignoring a transport operation this build does not know: {operation?.GetType()}."); // a newer master; the stream goes on
							break;
					}
				}
			}
			var reader = ObserveAsync(Reader());
			_readerTask = reader;

			// A known id (own, or resent as backlog) is not appended again: the local log keeps one record per event. The
			// resume point on the envelope is the cursor; it only ever moves forward, and it is persisted as it moves, after the
			// event is in the local log. The frame's own sequence caps it here as well: a master from before per-frame resume
			// points sends the tail on every backlog frame, and a run ending mid-replay would resume past what it never got.
			async Task ReceiveAsync(EventEnvelope envelope)
			{
				FramesReceived++;
				if (FramesReceived <= 3 || FramesReceived % 100 == 0)
				{
					EmergencyLog.Default.Stage($"replication: frame {FramesReceived} ({envelope.Event.GetType().Name} {envelope.Event.EventId})");
				}
				var ev = envelope.Event;
				var sequence = ev.SequenceId; // the master's stamp, read before the local log stamps its own
				var resumePoint = sequence != default && sequence.CompareBigEndian(envelope.Watermark) < 0 ? sequence : envelope.Watermark;
				bool known;
				lock (_skipSet)
				{
					known = !_skipSet.Add(ev.EventId);
					if (!known)
					{
						_skipList.AddLast(ev.EventId);
					}
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
				var isSubscribeTo = _config.HelloKind == ReplicationHelloKind.SubscribeTo;
				var helloSize = isSubscribeTo ? 41 : 25;
				var buffer = ArrayPool<byte>.Shared.Rent(helloSize);
				try
				{
					BitConverter.TryWriteBytes(buffer, _networkSerializationService.Magic);
					_eventReplicationState.ServerWatermark.TryWriteBytes(buffer.AsSpan(8, 16));
					buffer[24] = (byte)_config.HelloKind;
					if (isSubscribeTo)
					{
						(_config.InitialSubscribeStreamId ?? throw new InvalidOperationException("HelloKind SubscribeTo requires InitialSubscribeStreamId.")).TryWriteBytes(buffer.AsSpan(25, 16));
					}
					await wsConnection.SendAsync(new ArraySegment<byte>(buffer, 0, helloSize), WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
				}
				catch (WebSocketException ex)
				{
					// The socket broke before the master heard us: the reader sees it too and ends this connection.
					EmergencyLog.Default.LogError(ex, "EventReplicationService: the HELLO could not be sent; reconnecting.");
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(buffer);
				}
			}
			#endregion
			EmergencyLog.Default.Stage("replication: HELLO sent");
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
							held = _heldByMaster.Contains(ev.EventId);
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
			catch (WebSocketException ex)
			{
				// The socket broke under a send: this pass is over, the next connection sends it again.
				EmergencyLog.Default.LogError(ex, "EventReplicationService: a send failed; reconnecting.");
			}
			IsOnline = false;
			wsConnection.Abort();
			wsConnection.Dispose();
			await reader; // it logged its own failure
			failures = connectedAt.Elapsed > TimeSpan.FromSeconds(30) ? 0 : failures + 1; // a connection that held a while was not the problem
			if (!_cts.IsCancellationRequested)
			{
				await Task.WhenAny(Task.Delay(CalculateBackoff(failures), _cts.Token)); // a stop ends the pause, not the service with an exception
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

	/// <summary>Opens the socket, retrying with a growing pause while the master cannot be reached; only the host's stop ends the wait.</summary>
	async Task<ClientWebSocket> ConnectAsync()
	{
		ClientWebSocket? connected = null;
		for (var attempt = 0; connected is null; attempt++)
		{
			var socket = new ClientWebSocket();
			_config.ConfigureSocket?.Invoke(socket.Options);
			try
			{
				await socket.ConnectAsync(_config.ResolveEndpointUri(), _cts.Token);
				_wsConnection = socket;
				_networkSerializationService.Reinitialize();
				// Not online yet: that is declared only after the HELLO handshake completes,
				// when the master is guaranteed to have registered this node for broadcasts.
				connected = socket;
			}
			catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
			{
				socket.Dispose();
				EmergencyLog.Default.LogWarning(ex, $"EventReplicationService: connect attempt {attempt} failed: {ex.Message}");
				await Task.Delay(CalculateBackoff(attempt), _cts.Token);
			}
		}
		return connected;
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
		catch (Exception ex)
		{
			// Tolerable: the next connection resumes from the persisted cursor. A frame that fails every time shows as repeated errors.
			EmergencyLog.Default.LogError(ex, "EventReplicationService: the reader stopped on an exception; reconnecting.");
		}
		finally
		{
			IsOnline = false;
			EmergencyLog.Default.Stage($"replication: reader ended after {FramesReceived} frames");
			Wake(); // the send loop ends this connection
		}
	}

	/// <summary>The pause before connecting again: half a second, doubling with each failure in a row, at most half a minute.</summary>
	TimeSpan CalculateBackoff(int failures) => TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * (1 << Math.Min(failures, 6))));

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
