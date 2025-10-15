using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
public class EventReplicationService : IHostedService
{
	public const int DefaultFrameSize = 8192;

	private readonly IStorage<Event, Guid> _storage;
	private readonly EventReplicationState _eventReplicationState;
	private readonly JsonSerializerContext _jsonSerializerContext;
	private readonly Lazy<ISynqraStoreContext> _synqraStoreContext;
	private readonly EventReplicationConfig _config;

	private ClientWebSocket? _connection;

	private readonly INetworkSerializationService _networkSerializationService;

	private AutoResetEvent _autoResetEvent = new AutoResetEvent(false);
	private CancellationTokenSource _cts = new CancellationTokenSource();

	public bool IsOnline { get; private set; }

	public EventReplicationService(
		  IOptions<EventReplicationConfig> options
		, IStorage<Event, Guid> storage
		, EventReplicationState eventReplicationState
		, JsonSerializerContext jsonSerializerContext
		, Lazy<ISynqraStoreContext> synqraStoreContext
		, INetworkSerializationService? networkSerializationService = null
		, EventReplicationConfig? config = null
		)
	{
		_storage = storage;
		_eventReplicationState = eventReplicationState;
		_jsonSerializerContext = jsonSerializerContext;
		_synqraStoreContext = synqraStoreContext;
		_networkSerializationService = networkSerializationService ?? new JsonNetworkSerializationService();
		_config = config ?? options.Value;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await Task.Yield();
		_ = StartWorker();
	}

	HashSet<Guid> _skipSet = new HashSet<Guid>();
	LinkedList<Guid> _skipList = new LinkedList<Guid>();

	async Task ProcessEvent(Event @event)
	{
		if (_skipSet.Contains(@event.EventId))
		{
			return;
		}
		await @event.AcceptAsync<EventVisitorContext?>((IEventVisitor<EventVisitorContext?>)_synqraStoreContext.Value, null!);
		if (_skipSet.Add(@event.EventId))
		{
			_skipList.AddLast(@event.EventId);
		}
		Console.WriteLine();
	}

	static async Task<byte[]?> ReceiveFullMessageAsync(WebSocket ws, CancellationToken ct)
	{
		var rent = ArrayPool<byte>.Shared.Rent(DefaultFrameSize);
		try
		{
			using var ms = new MemoryStream(DefaultFrameSize);
			while (true)
			{
				var seg = new ArraySegment<byte>(rent);
				var res = await ws.ReceiveAsync(seg, ct);
				if (res.MessageType == WebSocketMessageType.Close)
				{
					return null;
				}

				ms.Write(rent, 0, res.Count);
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

	async Task StartWorker()
	{
		var ctx = _synqraStoreContext.Value ?? throw new ArgumentException();
		await foreach (var ev in _storage.GetAll(from: default))
		{
			await ev.AcceptAsync(ctx, null);
		}

		for (int i = 0; ; i++)
		{
			try
			{
				var ws = new ClientWebSocket();
				await ws.ConnectAsync(new Uri($"ws://localhost:{_config.Port}/api/synqra/ws"), _cts.Token);
				/*
				connection.On("NewEvent1", async (Event eventObject) =>
				{
					await ProcessEvent(eventObject);
				});
				*/
				// await _connection.StartAsync();
				_connection = ws;
				_networkSerializationService.Reinitialize();
				IsOnline = true;
				break;
			}
			catch (Exception ex) when (i < 10)
			{
				await Task.Delay(1000);
			}
		}
		async void Reader()
		{
			#region HELLO
			var magicBytes = await ReceiveFullMessageAsync(_connection, _cts.Token);
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
				var bytes = await ReceiveFullMessageAsync(_connection, _cts.Token);
				if (bytes == null || bytes.Length == 0)
				{
					IsOnline = false;
					break;
				}
				var operation = _networkSerializationService.Deserialize<TransportOperation>(bytes);
				EmergencyLog.Default.Debug("°9 Received: " + JsonSerializer.Serialize(operation, AppJsonContext.Default.TransportOperation));
				switch (operation)
				{
					case NewEvent1 ne1:
						await _storage.AppendAsync(ne1.Event);
						await ProcessEvent(ne1.Event);
						break;
					default:
						throw new NotSupportedException();
				}
			}
		}
		Reader();
		#region HELLO
		var buffer = ArrayPool<byte>.Shared.Rent(8);
		try
		{
			BitConverter.TryWriteBytes(buffer, _networkSerializationService.Magic);
			await _connection.SendAsync(new ArraySegment<byte>(buffer, 0, 8), WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
		#endregion
		var myEnumerable = _storage.GetAll(from: _eventReplicationState.LastEventIdFromMe);
		await using var myEnumerator = myEnumerable.GetAsyncEnumerator(_cts.Token);
		while (true)
		{
			_autoResetEvent.WaitOne();
			if (_cts.IsCancellationRequested)
			{
				break;
			}
			// get new events from storage and send them to server
			while (await myEnumerator.MoveNextAsync())
			{
				var ev = myEnumerator.Current;
				if (_skipSet.Add(ev.EventId))
				{
					_skipList.AddLast(ev.EventId);
				}
				// await _connection.InvokeAsync("NewEvent1", ev);

				var inv = new NewEvent1() { Event = ev };

				//var bytes = JsonSerializer.SerializeToUtf8Bytes<TransportOperation>(inv, AppJsonContext.Default.Options);

				var pool = ArrayPool<byte>.Shared;
				var bytes = pool.Rent(10240);
				// var span = new Span<byte>(bytes);
				try
				{
					var serialized = _networkSerializationService.Serialize<TransportOperation>(inv, bytes);
					await _connection.SendAsync(serialized, _networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
				}
				finally
				{
					pool.Return(bytes);
				}

				_eventReplicationState.LastEventIdFromMe = ev.EventId;
				_eventReplicationState.Save();
			}

			// get events from server and apply them locally
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		_cts.Cancel();
		_autoResetEvent.Set();
		// _ = _connection?.StopAsync();
		if (_connection != null && _connection.State == WebSocketState.Open)
		{
			await _connection.CloseAsync(WebSocketCloseStatus.Empty, null, cancellationToken);
		}
		// return Task.CompletedTask;
	}

	internal void Trigger(IReadOnlyList<Event> events)
	{
		_autoResetEvent.Set();
	}
}
