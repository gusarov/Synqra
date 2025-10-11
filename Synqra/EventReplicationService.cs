using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synqra.BinarySerializer;
using System;
using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets;

namespace Synqra;

/// <summary>
/// Client-side service that connect to WS Master
/// </summary>
public class EventReplicationService : IHostedService
{
	//private readonly IHubContext<SynqraSignalerHub, IEventHubClient> _hubContext;
	private readonly IStorage<Event, Guid> _storage;
	private readonly EventReplicationState _eventReplicationState;
	// private readonly JsonSerializerContext _jsonSerializerContext;
	private readonly Lazy<ISynqraStoreContext> _synqraStoreContext;
	private readonly EventReplicationConfig _config;
	private ClientWebSocket? _connection;

	private SBXSerializer? _sbxSerializerSender1 = new SBXSerializer();
	private ISBXSerializer? _sbxSerializerSender => _sbxSerializerSender1;

	private SBXSerializer? _sbxSerializerReceiver1 = new SBXSerializer();
	private ISBXSerializer? _sbxSerializerReceiver => _sbxSerializerReceiver1;


	private AutoResetEvent _autoResetEvent = new AutoResetEvent(false);
	private CancellationTokenSource _cts = new CancellationTokenSource();

	public bool IsOnline { get; private set; }

	public EventReplicationService(
		/*IHubContext<SynqraSignalerHub, IEventHubClient> hubContext
		 * , IOptions<EventReplicationConfig> options*/
		  EventReplicationConfig config
		, IStorage<Event, Guid> storage
		, EventReplicationState eventReplicationState
		// , JsonSerializerContext jsonSerializerContext
		, Lazy<ISynqraStoreContext> synqraStoreContext
		)
	{
		//_hubContext = hubContext;
		_storage = storage;
		_eventReplicationState = eventReplicationState;
		// _jsonSerializerContext = jsonSerializerContext;
		_synqraStoreContext = synqraStoreContext;
		_config = config;
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
		var rent = ArrayPool<byte>.Shared.Rent(64 * 1024);
		try
		{
			using var ms = new MemoryStream();
			while (true)
			{
				var seg = new ArraySegment<byte>(rent);
				var res = await ws.ReceiveAsync(seg, ct);
				if (res.MessageType == WebSocketMessageType.Close) return null;

				ms.Write(rent, 0, res.Count);
				if (res.EndOfMessage) break;
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
				_sbxSerializerSender1 = new SBXSerializer();
				_sbxSerializerReceiver1 = new SBXSerializer();
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
			while (!_cts.IsCancellationRequested)
			{
				var bytes = await ReceiveFullMessageAsync(_connection, _cts.Token);
				int pos = 0;
				var operation = _sbxSerializerSender1.Deserialize<TransportOperation>(bytes, ref pos);
				// var operation = JsonSerializer.Deserialize<TransportOperation>(Encoding.UTF8.GetString(bytes), _jsonSerializerContext.Options);
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
		// await _connection.InvokeAsync("Hello1", Guid.NewGuid(), 0L); // client send hello message to server. Parameters are: client id (guid me), last known event id (long)

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
				try
				{
					var span = new Span<byte>(bytes);
					// JsonSerializer.Serialize(span, inv, AppJsonContext.Default.Options);
					int pos = 0;
					_sbxSerializerSender1.Serialize(span, inv, ref pos);

					await _connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, _cts.Token);
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
