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

namespace Synqra;

/// <summary>
/// Client-side service that connect to WS Master
/// </summary>
public class EventReplicationService : BackgroundService, IEventReplicationService
{
	public const int DefaultFrameSize = 8192;

	private readonly IAppendStorage<Event, Guid> _storage;
	private readonly EventReplicationState _eventReplicationState;
	private readonly JsonSerializerContext? _jsonSerializerContext;
	private readonly EventReplicationConfig _config;

	private readonly INetworkSerializationService _networkSerializationService;

	private SemaphoreSlim _autoResetEvent = new SemaphoreSlim(0, 1);
	private CancellationTokenSource _cts = new CancellationTokenSource();

	volatile bool _isOnline;
	public bool IsOnline { get => _isOnline; private set => _isOnline = value; }

	private Task? _readerTask;
	private ClientWebSocket? _wsConnection;
	private volatile IReadOnlyCollection<Guid> _activeStreams = Array.Empty<Guid>();

	/// <inheritdoc />
	public IReadOnlyCollection<Guid> ActiveStreams => _activeStreams;

	/// <inheritdoc />
	public event Action? SubscriptionChanged;

	/// <inheritdoc />
	public event Action? EventsReceived;

	/// <inheritdoc />
	public Task SubscribeAsync(Guid streamId, CancellationToken ct = default)
		=> SendStreamControlAsync(ReplicationFrameTag.Subscribe, streamId, ct);

	/// <inheritdoc />
	public Task UnsubscribeAsync(Guid streamId, CancellationToken ct = default)
		=> SendStreamControlAsync(ReplicationFrameTag.Unsubscribe, streamId, ct);

	private async Task SendStreamControlAsync(ReplicationFrameTag tag, Guid streamId, CancellationToken ct)
	{
		var socket = _wsConnection ?? throw new InvalidOperationException("Not connected — cannot change subscriptions before the replication client has connected.");
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
		await socket.SendStreamControlFrameAsync(tag, streamId, _networkSerializationService.IsTextOrBinary, linked.Token);
	}

	public EventReplicationService(
		  IOptions<EventReplicationConfig> options
		, IAppendStorage<Event, Guid> storage
		, EventReplicationState eventReplicationState
		, INetworkSerializationService networkSerializationService
		, JsonSerializerContext? jsonSerializerContext = null
		, EventReplicationConfig? config = null
		)
	{
		_storage = storage;
		_eventReplicationState = eventReplicationState;
		_networkSerializationService = networkSerializationService;
		_jsonSerializerContext = jsonSerializerContext;
		_config = config ?? options.Value;
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
			#endregion
			while (!_cts.IsCancellationRequested)
			{
				var bytes = await ReceiveFullMessageAsync(wsConnection, _cts.Token);
				if (bytes == null || bytes.Length == 0)
				{
					IsOnline = false;
					break;
				}
				var frameTag = (ReplicationFrameTag)bytes[0];
				if (frameTag == ReplicationFrameTag.SubscriptionState)
				{
					// Master's authoritative snapshot of the streams this connection is now subscribed
					// to (N*16 bytes after the tag). Lets the client detect an unexpected default and
					// drive its own UI off the confirmed set. Sent after HELLO and every sub/unsub.
					var count = (bytes.Length - 1) / 16;
					var streams = new Guid[count];
					for (var i = 0; i < count; i++)
					{
						streams[i] = new Guid(bytes.AsSpan(1 + (i * 16), 16));
					}
					_activeStreams = streams;
					SubscriptionChanged?.Invoke();
					continue;
				}
				if (frameTag != ReplicationFrameTag.Event)
				{
					throw new NotSupportedException($"Unsupported replication frame tag: {frameTag}.");
				}
				var operation = _networkSerializationService.Deserialize<TransportOperation>(bytes.AsSpan(1));
				switch (operation)
				{
					case NewEvent1 ne1:
						await _storage.AppendAsync(ne1.Event);
						EmergencyLog.Default.LogInformation($"{GetHashCode():X4} <<< {ne1.Event}");
						// Transport-only: dedup so the outgoing loop never echoes a server event back,
						// then notify the projection owner to fold in the delta via the keeper. The
						// event is already durably stored, so a missed/awaited-late notification is
						// harmless — the owner also catches up on next use through the provider.
						lock (_skipSet)
						{
							if (_skipSet.Add(ne1.Event.EventId))
							{
								_skipList.AddLast(ne1.Event.EventId);
							}
						}
						// Tracks how far this client's backlog catch-up has progressed —
						// applies to both a genuine live event and one the server resent as
						// backlog (see the HELLO region above); mirrors LastEventIdFromMe's
						// own bookkeeping for outgoing events.
						_eventReplicationState.LastEventIdFromServer = ne1.Event.EventId;
						EventsReceived?.Invoke();
						break;
					default:
						throw new NotSupportedException();
				}
			}
		}
		_readerTask = Reader();

		#region HELLO
		{
			// 8 bytes magic + 16 bytes LastEventIdFromServer (Guid.Empty = "send me everything") + the
			// HELLO "ws-method": byte[24] kind, and for Hello_SubscribeTo the next 16 bytes are the
			// stream id (total 41 bytes). See SynqraReplicationEndpointExtensions.cs for the server side.
			var isSubscribeTo = _config.HelloKind == ReplicationHelloKind.SubscribeTo;
			var helloSize = isSubscribeTo ? 41 : 25;
			var buffer = ArrayPool<byte>.Shared.Rent(helloSize);
			try
			{
				BitConverter.TryWriteBytes(buffer, _networkSerializationService.Magic);
				_eventReplicationState.LastEventIdFromServer.TryWriteBytes(buffer.AsSpan(8, 16));
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

		var myEnumerable = _storage.GetAllAsync(from: _eventReplicationState.LastEventIdFromMe);
		await using var myEnumerator = myEnumerable.GetAsyncEnumerator(_cts.Token);
		while (true)
		{
			await _autoResetEvent.WaitAsync();
			EmergencyLog.Default.LogInformation($"{GetHashCode():X4} >>> <TRIGGERED LOOP>");
			if (_cts.IsCancellationRequested)
			{
				break;
			}
			// get new events from storage and send them to server
			while (await myEnumerator.MoveNextAsync())
			{
				var ev = myEnumerator.Current;
				// Single-stream connection: skip events belonging to any other stream that shares this
				// local multitenant store (e.g. the primary stream's events when this connection only
				// replicates a "/tracking" sub-stream). The outbound cursor still advances past them so
				// they are never re-examined, and they are never sent to — and mis-stamped by — the
				// stream-scoped master endpoint. StreamId is set locally even though it is off the wire.
				if (_config.StreamId is Guid onlyStream && ev.StreamId != onlyStream)
				{
					_eventReplicationState.LastEventIdFromMe = ev.EventId;
					continue;
				}
				lock (_skipSet)
				{
					if (_skipSet.Add(ev.EventId))
					{
						_skipList.AddLast(ev.EventId);
					}
				}
				// await _connection.InvokeAsync("NewEvent1", ev);

				var inv = new NewEvent1() { Event = ev };

				//var bytes = JsonSerializer.SerializeToUtf8Bytes<TransportOperation>(inv, AppJsonContext.Default.Options);

				var pool = ArrayPool<byte>.Shared;
				var bytes = pool.Rent(DefaultFrameSize);
				// var span = new Span<byte>(bytes);
				try
				{
					// [1 tag byte][SBX payload] — payload written at offset 1 so the Event tag prefixes
					// it without a second copy. Matches the server's framing (ReplicationFrameTag).
					var serialized = _networkSerializationService.Serialize<TransportOperation>(inv, new ArraySegment<byte>(bytes, 1, bytes.Length - 1));
					bytes[0] = (byte)ReplicationFrameTag.Event;
					EmergencyLog.Default.LogInformation($"{GetHashCode():X4} >>> {ev}");
					await wsConnection.SendAsync(new ArraySegment<byte>(bytes, 0, serialized.Count + 1), _networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
				}
				finally
				{
					pool.Return(bytes);
				}

				_eventReplicationState.LastEventIdFromMe = ev.EventId;
			}
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

	public void Trigger(Command command, IReadOnlyList<Event> events)
	{
		try
		{
			_autoResetEvent.Release();
		}
		catch (SemaphoreFullException)
		{
			// already signaled - concurrent triggers coalesce into one loop iteration
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
		_eventReplicationState.LastEventIdFromServer = default;
		if (_storage is IClearableAppendStorage clearable)
		{
			await clearable.ClearAllAsync();
		}
	}
}
