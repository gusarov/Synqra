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
	private readonly Lazy<IProjection> _synqraStoreContext;
	private readonly EventReplicationConfig _config;

	private readonly INetworkSerializationService _networkSerializationService;

	private SemaphoreSlim _autoResetEvent = new SemaphoreSlim(0, 1);
	private CancellationTokenSource _cts = new CancellationTokenSource();

	volatile bool _isOnline;
	public bool IsOnline { get => _isOnline; private set => _isOnline = value; }

	private Task? _readerTask;

	public EventReplicationService(
		  IOptions<EventReplicationConfig> options
		, IAppendStorage<Event, Guid> storage
		, EventReplicationState eventReplicationState
		, Lazy<IProjection> synqraStoreContext
		, INetworkSerializationService networkSerializationService
		, JsonSerializerContext? jsonSerializerContext = null
		, EventReplicationConfig? config = null
		)
	{
		_storage = storage;
		_eventReplicationState = eventReplicationState;
		_synqraStoreContext = synqraStoreContext;
		_networkSerializationService = networkSerializationService;
		_jsonSerializerContext = jsonSerializerContext;
		_config = config ?? options.Value;
	}

	HashSet<Guid> _skipSet = new HashSet<Guid>();
	LinkedList<Guid> _skipList = new LinkedList<Guid>();

	async Task ProcessEvent(Event @event)
	{
		lock (_skipSet)
		{
			if (_skipSet.Contains(@event.EventId))
			{
				return;
			}
		}
		await @event.AcceptAsync<EventVisitorContext?>((IEventVisitor<EventVisitorContext?>)_synqraStoreContext.Value, null!);
		lock (_skipSet)
		{
			if (_skipSet.Add(@event.EventId))
			{
				_skipList.AddLast(@event.EventId);
			}
		}
	}

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
		var ctx = _synqraStoreContext.Value ?? throw new ArgumentException();
		await foreach (var ev in _storage.GetAllAsync(from: default))
		{
			await ev.AcceptAsync(ctx, null);
		}

		ClientWebSocket wsConnection;
		for (int i = 0; ; i++)
		{
			try
			{
				wsConnection = new ClientWebSocket();
				await wsConnection.ConnectAsync(new Uri($"ws://localhost:{_config.Port}/api/synqra/ws"), _cts.Token);
				_networkSerializationService.Reinitialize();
				IsOnline = true;
				break;
			}
			catch (Exception ex) when (i < 10)
			{
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
					case NewEvent1 ne1:
						await _storage.AppendAsync(ne1.Event);
						EmergencyLog.Default.LogInformation($"{GetHashCode():X4} <<< {ne1.Event}");
						await ProcessEvent(ne1.Event);
						break;
					default:
						throw new NotSupportedException();
				}
			}
		}
		_readerTask = Reader();

		#region HELLO
		{
			var buffer = ArrayPool<byte>.Shared.Rent(8);
			try
			{
				BitConverter.TryWriteBytes(buffer, _networkSerializationService.Magic);
				await wsConnection.SendAsync(new ArraySegment<byte>(buffer, 0, 8), WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
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
					var serialized = _networkSerializationService.Serialize<TransportOperation>(inv, bytes);
					EmergencyLog.Default.LogInformation($"{GetHashCode():X4} >>> {ev}");
					await wsConnection.SendAsync(serialized, _networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
				}
				finally
				{
					pool.Return(bytes);
				}

				_eventReplicationState.LastEventIdFromMe = ev.EventId;
				_eventReplicationState.Save();
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
}
