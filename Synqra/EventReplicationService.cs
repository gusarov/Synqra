using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Net.WebSockets;

namespace Synqra;

public sealed class EventReplicationService : BackgroundService, IEventReplicationService
{
	public const int DefaultFrameSize = 8192;

	private readonly IClientEventStore _storage;
	private readonly INetworkSerializationService _networkSerializationService;
	private readonly ReplicationProtocol _protocol;
	private readonly EventReplicationConfig _config;
	private readonly SemaphoreSlim _pendingSignal = new(0, 1);
	private readonly object _connectionLock = new();
	private CancellationTokenSource? _connectionCts;
	private volatile bool _isOnline;

	public EventReplicationService(
		  IOptions<EventReplicationConfig> options
		, IClientEventStore storage
		, INetworkSerializationService networkSerializationService
		, ReplicationProtocol protocol
		, EventReplicationConfig? config = null
	)
	{
		_storage = storage;
		_networkSerializationService = networkSerializationService;
		_protocol = protocol;
		_config = config ?? options.Value;
	}

	public bool IsOnline => _isOnline;

	public event Action? EventsReceived;
	public event Action? RebuildRequired;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			using var wsConnection = new ClientWebSocket();
			using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
			lock (_connectionLock)
			{
				_connectionCts = connectionCts;
			}

			try
			{
				var streamId = await ResolveStreamIdAsync(connectionCts.Token);
				if (streamId == Guid.Empty
					|| _config.ConfigureWebSocketAsync is not null
					&& !await _config.ConfigureWebSocketAsync(wsConnection, connectionCts.Token)
				)
				{
					await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
					continue;
				}

				await wsConnection.ConnectAsync(_config.ResolveEndpointUri(), connectionCts.Token);
				await RepairConfirmedEventsAsync(wsConnection, streamId, connectionCts.Token);
				_isOnline = true;

				var readerTask = ReadAsync(wsConnection, streamId, connectionCts.Token);
				var writerTask = WriteAsync(wsConnection, streamId, connectionCts.Token);
				await Task.WhenAny(readerTask, writerTask);
				await connectionCts.CancelAsync();
				await Task.WhenAll(readerTask, writerTask);
			}
			catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				EmergencyLog.Default.LogWarning(ex, $"EventReplicationService: connection failed: {ex.Message}");
			}
			finally
			{
				_isOnline = false;
				lock (_connectionLock)
				{
					if (ReferenceEquals(_connectionCts, connectionCts))
					{
						_connectionCts = null;
					}
				}
			}

			if (!stoppingToken.IsCancellationRequested)
			{
				await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
			}
		}
	}

	public async Task StageAsync(
		  Command command
		, IReadOnlyList<Event> events
		, CommandSubmissionOptions? options = null
		, CancellationToken cancellationToken = default
	)
	{
		await _storage.StageAsync(command, events, options, cancellationToken);
		SignalPending();
	}

	public async Task ReconnectAsync()
	{
		CancellationTokenSource? connectionCts;
		lock (_connectionLock)
		{
			connectionCts = _connectionCts;
		}

		if (connectionCts is not null)
		{
			await connectionCts.CancelAsync();
		}
	}

	private async Task<Guid> ResolveStreamIdAsync(CancellationToken cancellationToken)
	{
		if (_config.ResolveStreamIdAsync is null)
		{
			throw new InvalidOperationException("EventReplicationConfig.ResolveStreamIdAsync is required for client replication.");
		}
		return await _config.ResolveStreamIdAsync(cancellationToken) ?? Guid.Empty;
	}

	private async Task RepairConfirmedEventsAsync(
		  ClientWebSocket wsConnection
		, Guid streamId
		, CancellationToken cancellationToken
	)
	{
		var digests = new List<ConfirmedEventDigest>();
		await foreach (var digest in _storage.GetConfirmedDigestsAsync(streamId, cancellationToken))
		{
			digests.Add(digest);
		}

		await SendAsync(
			wsConnection,
			_protocol.CreateHello(_networkSerializationService.Magic, digests),
			cancellationToken
		);
		var accepted = await ReceiveFullMessageAsync(wsConnection, cancellationToken)
			?? throw new InvalidOperationException("Replication server closed before accepting the hello frame.");
		_protocol.ValidateHelloAccepted(accepted, _networkSerializationService.Magic);

		var changed = false;
		while (true)
		{
			var frame = await ReceiveFullMessageAsync(wsConnection, cancellationToken)
				?? throw new InvalidOperationException("Replication server closed during confirmed-record repair.");
			switch (_protocol.ReadKind(frame))
			{
				case ReplicationFrameKind.ConfirmedEvent:
					var ev = _protocol.ReadConfirmedEvent(frame);
					ev.StreamId = streamId;
					changed |= await _storage.UpsertConfirmedAsync(ev, cancellationToken)
						!= ClientEventStoreChange.None;
					break;
				case ReplicationFrameKind.DeleteConfirmedEvent:
					changed |= await _storage.DeleteConfirmedAsync(
						_protocol.ReadEventId(frame, ReplicationFrameKind.DeleteConfirmedEvent),
						cancellationToken
					) != ClientEventStoreChange.None;
					break;
				case ReplicationFrameKind.RepairComplete:
					if (changed)
					{
						RebuildRequired?.Invoke();
					}
					return;
				default:
					throw new InvalidDataException("Unexpected frame during confirmed-record repair.");
			}
		}
	}

	private async Task ReadAsync(
		  ClientWebSocket wsConnection
		, Guid streamId
		, CancellationToken cancellationToken
	)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			var frame = await ReceiveFullMessageAsync(wsConnection, cancellationToken);
			if (frame is null)
			{
				return;
			}

			switch (_protocol.ReadKind(frame))
			{
				case ReplicationFrameKind.ConfirmedEvent:
					var ev = _protocol.ReadConfirmedEvent(frame);
					ev.StreamId = streamId;
					var upsertChange = await _storage.UpsertConfirmedAsync(ev, cancellationToken);
					if (upsertChange == ClientEventStoreChange.Append)
					{
						EventsReceived?.Invoke();
					}
					else if (upsertChange == ClientEventStoreChange.Rebuild)
					{
						RebuildRequired?.Invoke();
					}
					break;
				case ReplicationFrameKind.DeleteConfirmedEvent:
					if (await _storage.DeleteConfirmedAsync(
							_protocol.ReadEventId(frame, ReplicationFrameKind.DeleteConfirmedEvent),
							cancellationToken
						) == ClientEventStoreChange.Rebuild)
					{
						RebuildRequired?.Invoke();
					}
					break;
				case ReplicationFrameKind.CommandAcknowledged:
					if (await _storage.AcknowledgeAsync(
							_protocol.ReadEventId(frame, ReplicationFrameKind.CommandAcknowledged),
							cancellationToken
						) == ClientEventStoreChange.Rebuild)
					{
						RebuildRequired?.Invoke();
					}
					break;
				case ReplicationFrameKind.CommandRejected:
					var rejected = _protocol.ReadCommandRejected(frame);
					EmergencyLog.Default.LogWarning(
						$"EventReplicationService: command {rejected.CommandId} was rejected: {rejected.Message}"
					);
					break;
				default:
					throw new InvalidDataException("Unexpected replication frame after repair completed.");
			}
		}
	}

	private async Task WriteAsync(
		  ClientWebSocket wsConnection
		, Guid streamId
		, CancellationToken cancellationToken
	)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await SendPendingAsync(wsConnection, streamId, cancellationToken);
			await _pendingSignal.WaitAsync(cancellationToken);
		}
	}

	private async Task SendPendingAsync(
		  ClientWebSocket wsConnection
		, Guid streamId
		, CancellationToken cancellationToken
	)
	{
		await foreach (var batch in _storage.GetPendingAsync(streamId, cancellationToken))
		{
			await SendAsync(
				wsConnection,
				_protocol.CreateSubmitCommand(batch),
				cancellationToken
			);
		}
	}

	private void SignalPending()
	{
		try
		{
			_pendingSignal.Release();
		}
		catch (SemaphoreFullException)
		{
		}
	}

	private async Task<byte[]?> ReceiveFullMessageAsync(WebSocket ws, CancellationToken cancellationToken)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(DefaultFrameSize);
		try
		{
			using var stream = new MemoryStream(DefaultFrameSize);
			while (!cancellationToken.IsCancellationRequested)
			{
				var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					return null;
				}
				if (result.Count > 0)
				{
					stream.Write(buffer, 0, result.Count);
				}
				if (result.EndOfMessage)
				{
					return stream.ToArray();
				}
			}
			return null;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private async Task SendAsync(
		  WebSocket ws
		, byte[] frame
		, CancellationToken cancellationToken
	)
	{
		await ws.SendAsync(
			frame,
			WebSocketMessageType.Binary,
			endOfMessage: true,
			cancellationToken
		);
	}
}
