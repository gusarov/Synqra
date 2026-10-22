using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage;
using System;
using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

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
	private volatile IReadOnlyCollection<Guid> _activeStreams = Array.Empty<Guid>();

	/// <inheritdoc />
	public IReadOnlyCollection<Guid> ActiveStreams => _activeStreams;

	/// <inheritdoc />
	public event Action? EventsReceived;

	/// <summary>Event frames this run received from the server, known ones included: what a resumed client was sent again.</summary>
	public long FramesReceived { get; private set; }

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

	async Task SaveStateAsync()
	{
		if (_stateStore is not null)
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
			await foreach (var ev in _storage.GetAllAsync(from: default))
			{
				lock (_skipSet)
				{
					if (_skipSet.Add(ev.EventId))
					{
						_skipList.AddLast(ev.EventId);
					}
				}
			}
		}
		catch (Exception ex)
		{
			EmergencyLog.Default.LogWarning(ex, "EventReplicationService: local durable log failed to replay — clearing it and resyncing fresh from the server.");
			await ClearLocalStorageAsync();
		}

		bool logHoldsEvents;
		lock (_skipSet)
		{
			logHoldsEvents = _skipSet.Count > 0;
		}
		await LoadStateAsync(logHoldsEvents); // the cursor of the last run: HELLO asks for the delta, not the stream
		_lastSentEventId = _eventReplicationState.LastEventIdFromMe; // this run offers everything the master has not acknowledged

		ClientWebSocket wsConnection;
		for (int i = 0; ; i++)
		{
			try
			{
				wsConnection = new ClientWebSocket();
				_config.ConfigureSocket?.Invoke(wsConnection.Options);
				await wsConnection.ConnectAsync(_config.ResolveEndpointUri(), _cts.Token);
				_wsConnection = wsConnection;
				_networkSerializationService.Reinitialize();
				// Not online yet — that is declared only after the HELLO handshake completes,
				// when the master is guaranteed to have registered this node for broadcasts.
				break;
			}
			catch (Exception ex) when (i < 10)
			{
				EmergencyLog.Default.LogWarning(ex, $"EventReplicationService: connect attempt {i} failed: {ex.Message}");
				await Task.Delay(1000);
			}
		}

		// Frames read off the socket wait here for the processor. On a slow CPU they pile up while a batch is being stored, and
		// the next batch takes all of them: the local log, the state store and the owner's catch-up are visited once per batch,
		// not once per event, so a client replaying a long backlog catches up at the speed of the network rather than of
		// its storage. A batch is only what has already arrived: a live event is handled as soon as the previous batch is done.
		var maxBatch = Math.Max(1, _config.MaxReceiveBatch);
		var received = Channel.CreateBounded<TransportOperation>(new BoundedChannelOptions(maxBatch)
		{
			SingleReader = true,
			SingleWriter = true,
			FullMode = BoundedChannelFullMode.Wait,
		});

		async Task Reader()
		{
			try
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
				#endregion
				var open = true;
				while (open && !_cts.IsCancellationRequested)
				{
					var bytes = await ReceiveFullMessageAsync(wsConnection, _cts.Token);
					open = bytes != null && bytes.Length > 0;
					if (open)
					{
						await received.Writer.WriteAsync(_networkSerializationService.Deserialize<TransportOperation>(bytes!), _cts.Token);
					}
				}
			}
			finally
			{
				received.Writer.TryComplete(); // the processor handles what came before the close, then the node is offline
			}
		}

		async Task Processor()
		{
			var batch = new List<TransportOperation>(maxBatch);
			while (!_cts.IsCancellationRequested && await received.Reader.WaitToReadAsync())
			{
				while (batch.Count < maxBatch && received.Reader.TryRead(out var operation))
				{
					batch.Add(operation);
				}
				await ReceiveBatchAsync(batch);
				batch.Clear();
			}
			IsOnline = false;
		}
		_readerTask = Task.WhenAll(Processor(), Reader());

		// A batch keeps the order of its frames: an event's stream is told by the subscription state that came before it, and
		// the acks move LastEventIdFromMe in the order they came. Its new events go to the local log first; the state is then
		// saved once, with the newest cursors; the owner is then told once.
		async Task ReceiveBatchAsync(IReadOnlyList<TransportOperation> batch)
		{
			var arrived = new List<Event>();
			var watermark = _eventReplicationState.ServerWatermark;
			var acknowledged = false;
			foreach (var operation in batch)
			{
				switch (operation)
				{
					case SubscriptionState state:
						// Master's authoritative snapshot of the streams this connection is now
						// subscribed to. Lets the client detect an unexpected default and drive its
						// own UI off the confirmed set. Sent after HELLO and every sub/unsub.
						_activeStreams = state.Streams as IReadOnlyCollection<Guid> ?? state.Streams.ToArray();
						break;
					case EventEnvelope envelope:
						watermark = Receive(envelope, watermark, arrived);
						break;
					case EventAccepted accepted:
						// Acks arrive in the order the events were sent, so the latest one is the cursor.
						_eventReplicationState.LastEventIdFromMe = accepted.EventId;
						acknowledged = true;
						break;
					case EventRejected rejected:
						// Refused for good: the cursor moves past it like on an ack, so it is not offered on every connect; its local effect stays until the master's events for the same state arrive.
						EmergencyLog.Default.LogWarning($"EventReplicationService: the master refused event {rejected.EventId}: {rejected.Reason}");
						_eventReplicationState.LastEventIdFromMe = rejected.EventId;
						acknowledged = true;
						break;
					default:
						EmergencyLog.Default.LogWarning($"EventReplicationService: ignoring a transport operation this build does not know: {operation?.GetType()}."); // a newer master; the stream goes on
						break;
				}
			}
			await AppendArrivedAsync(arrived);
			var advanced = watermark.CompareBigEndian(_eventReplicationState.ServerWatermark) > 0;
			if (advanced)
			{
				_eventReplicationState.ServerWatermark = watermark; // only now: a save from elsewhere persists no cursor past the local log
			}
			if (advanced || acknowledged)
			{
				await SaveStateAsync();
			}
			if (arrived.Count > 0)
			{
				EventsReceived?.Invoke(); // the owner folds the delta in through the keeper
			}
		}

		// A known id (own, or resent as backlog) is not appended again: the local log keeps one record per event. The
		// resume point on the envelope is the cursor; it only ever moves forward, and it is persisted as it moves, after the
		// events that moved it are in the local log. The frame's own sequence caps it here as well: a master from before
		// per-frame resume points sends the tail on every backlog frame, and a run ending mid-replay would resume past what it
		// never got. Returns the batch's watermark with this frame's resume point taken in.
		Guid Receive(EventEnvelope envelope, Guid watermark, List<Event> arrived)
		{
			FramesReceived++;
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
			}
			if (!known)
			{
				ev.StreamId = StreamOf(ev);
				arrived.Add(ev);
			}
			return resumePoint.CompareBigEndian(watermark) > 0 ? resumePoint : watermark;
		}

		// Each run of consecutive events of one stream is one append: a log's batch belongs to one stream.
		async Task AppendArrivedAsync(List<Event> arrived)
		{
			var start = 0;
			while (start < arrived.Count)
			{
				var end = start + 1;
				while (end < arrived.Count && arrived[end].StreamId == arrived[start].StreamId)
				{
					end++;
				}
				await _storage.AppendBatchAsync(arrived.GetRange(start, end - start)); // the local log stamps its own arrival order over the server's sequence
				start = end;
			}
			foreach (var ev in arrived)
			{
				EmergencyLog.Default.LogInformation($"{GetHashCode():X4} <<< {ev}");
			}
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
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
		#endregion
		Wake(); // whatever the last run left unacknowledged goes now, not at the next local write

		while (true)
		{
			await _autoResetEvent.WaitAsync();
			EmergencyLog.Default.LogInformation($"{GetHashCode():X4} >>> <TRIGGERED LOOP>");
			if (_cts.IsCancellationRequested)
			{
				break;
			}
			// get new events from storage and send them to server
			// A fresh read per trigger: a storage's enumeration ends once it runs dry (IndexedDb stops at an empty
			// page), so one enumerator kept across triggers sent nothing after the first drain. A full read, skipping
			// up to the last sent id, as the keeper does: `from` is not a uniform range bound across backends.
			var sent = _lastSentEventId;
			var reached = sent == default;
			await foreach (var ev in _storage.GetAllAsync(from: default, _cts.Token))
			{
				if (!reached)
				{
					reached = ev.EventId == sent;
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
					EmergencyLog.Default.LogInformation($"{GetHashCode():X4} >>> {ev}");
					await wsConnection.SendOperationAsync(_networkSerializationService, inv, bytes, _cts.Token);
				}
				finally
				{
					pool.Return(bytes);
				}

				_lastSentEventId = ev.EventId; // written, not yet acknowledged: the master's EventAccepted moves the persisted cursor
			}
			await SaveStateAsync();
			EmergencyLog.Default.LogInformation($"{GetHashCode():X4} >>> </LOOP>");

			// get events from server and apply them locally
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

	/// <inheritdoc />
	public async Task ClearLocalStorageAsync()
	{
		lock (_skipSet)
		{
			_skipSet.Clear();
			_skipList.Clear();
		}
		_eventReplicationState.ServerWatermark = default;
		_eventReplicationState.LastEventIdFromMe = default;
		if (_storage is IClearableAppendStorage clearable)
		{
			await clearable.ClearAllAsync();
		}
		await SaveStateAsync();
	}
}
