using Microsoft.AspNet.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Threading;

namespace Synqra;

/// <summary>
/// Client-side service that connect to WS Master
/// </summary>
public class EventReplicationService : IHostedService
{
	//private readonly IHubContext<SynqraSignalerHub, IEventHubClient> _hubContext;
	private readonly IStorage<Event, Guid> _storage;
	private readonly EventReplicationState _eventReplicationState;
	private readonly Lazy<ISynqraStoreContext> _synqraStoreContext;
	private readonly EventReplicationConfig _config;
	private HubConnection? _connection;
	private AutoResetEvent _autoResetEvent = new AutoResetEvent(false);
	private CancellationTokenSource _cts = new CancellationTokenSource();

	public EventReplicationService(/*IHubContext<SynqraSignalerHub, IEventHubClient> hubContext, */IOptions<EventReplicationConfig> options, IStorage<Event, Guid> storage, EventReplicationState eventReplicationState, Lazy<ISynqraStoreContext> synqraStoreContext)
	{
		//_hubContext = hubContext;
		_storage = storage;
		_eventReplicationState = eventReplicationState;
		_synqraStoreContext = synqraStoreContext;
		_config = options.Value;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await Task.Yield();
		_ = StartWorker();
	}

	async Task StartWorker()
	{
		var ctx = _synqraStoreContext.Value ?? throw new ArgumentException();
		await foreach (var ev in _storage.GetAll(from: default))
		{
			await ev.AcceptAsync(ctx, null);
		}

		var connection = _connection = new HubConnectionBuilder()
			.WithUrl($"http://localhost:{_config.Port}/api/synqra")
			.WithAutomaticReconnect()
			.Build();
		connection.On<Event>("NewEvent", async (eventObject) =>
		{
			throw new NotImplementedException();
			// await _storage.AppendAsync(new[] { eventObject }, _cts.Token);
		});
		await connection.StartAsync();
		await connection.InvokeAsync("Hello1", Guid.NewGuid(), 0L); // client send hello message to server. Parameters are: client id (guid me), last known event id (long)

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
				await connection.InvokeAsync("NewEvent", ev);
				_eventReplicationState.LastEventIdFromMe = ev.EventId;
				_eventReplicationState.Save();
			}

			// get events from server and apply them locally
		}
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_cts.Cancel();
		_autoResetEvent.Set();
		_ = _connection?.StopAsync();
		return Task.CompletedTask;
	}

	internal void Trigger(IReadOnlyList<Event> events)
	{
		_autoResetEvent.Set();
	}
}