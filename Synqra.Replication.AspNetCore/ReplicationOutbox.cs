using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Synqra.Replication.AspNetCore;

/// <summary>
/// One replication socket's sends. A WebSocket takes one send at a time; the endpoint used to provide that by holding every
/// socket's sends under its one global gate, so a client taking its snapshot or backlog stalled every other socket's pushes for as
/// long as its own network took. Here:
/// <list type="bullet">
/// <item>a push (another connection's event, a REST write) is queued with <see cref="Push"/> and written by this socket's own pump
/// (<see cref="PumpAsync"/>): a broadcaster never waits on a peer's network;</item>
/// <item>the connection's own frames (its HELLO reply, its catch-up, its acks) take turns with the pump's (<see cref="SendAsync"/>);</item>
/// <item>while the connection catches up, its pushes wait (<see cref="HoldPushesAsync"/>) and follow in commit order after it, so a
/// push never overtakes the backlog before it. A push the backlog also carried arrives twice, and the client keeps one.</item>
/// </list>
/// A client <see cref="Capacity"/> pushes behind is cut off. It reconnects and resumes from its watermark; an unbounded queue would
/// keep its pushes in the master's memory for as long as it stalls.
/// </summary>
internal sealed class ReplicationOutbox
{
	readonly WebSocket _socket;
	readonly INetworkSerializationService _serializer;
	readonly ILogger? _logger;
	readonly SemaphoreSlim _sending = new(1, 1);
	readonly SemaphoreSlim _pushesFlow = new(1, 1);
	readonly Channel<EventEnvelope> _pushes;
	readonly byte[] _buffer = new byte[EventReplicationService.DefaultFrameSize];

	public ReplicationOutbox(WebSocket socket, INetworkSerializationService serializer, ILogger? logger, int capacity = 10_000)
	{
		_socket = socket;
		_serializer = serializer;
		_logger = logger;
		Capacity = capacity;
		_pushes = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(capacity)
		{
			SingleReader = true,
			FullMode = BoundedChannelFullMode.Wait, // TryWrite then refuses: the client is cut off, never waited for
		});
	}

	/// <summary>The pushes a client may fall behind by before it is cut off.</summary>
	public int Capacity { get; }

	/// <summary>Queues a push for this socket; never waits. A client too far behind is cut off and resumes on its next connect.</summary>
	public void Push(Event ev, Guid watermark)
	{
		if (!_pushes.Writer.TryWrite(new EventEnvelope { Event = ev, Watermark = watermark }) && _socket.State == WebSocketState.Open)
		{
			_logger?.LogWarning("Synqra replication: a client fell {Capacity} pushes behind; it is cut off and resumes from its watermark when it reconnects.", Capacity);
			_socket.Abort();
		}
	}

	/// <summary>Sends one of the connection's own frames, in turn with the pump.</summary>
	public async Task SendAsync(TransportOperation operation, CancellationToken cancellationToken)
	{
		await _sending.WaitAsync(cancellationToken);
		try
		{
			await _socket.SendOperationAsync(_serializer, operation, _buffer, cancellationToken);
		}
		finally
		{
			_sending.Release();
		}
	}

	/// <summary>Sends raw bytes (the HELLO reply's magic), in turn with the pump.</summary>
	public async Task SendBytesAsync(byte[] bytes, CancellationToken cancellationToken)
	{
		await _sending.WaitAsync(cancellationToken);
		try
		{
			await _socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
		}
		finally
		{
			_sending.Release();
		}
	}

	/// <summary>Holds this socket's pushes until the returned scope ends: they queue, and the pump writes them after.</summary>
	public async Task<IDisposable> HoldPushesAsync(CancellationToken cancellationToken)
	{
		await _pushesFlow.WaitAsync(cancellationToken);
		return new PushHold(_pushesFlow);
	}

	/// <summary>
	/// Writes the queued pushes, in order, until <see cref="Complete"/> or the connection ends. A send that fails ends it: the peer is
	/// gone, and the pushes after it are dropped with the connection.
	/// </summary>
	public async Task PumpAsync(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var envelope in _pushes.Reader.ReadAllAsync(cancellationToken))
			{
				await _pushesFlow.WaitAsync(cancellationToken);
				try
				{
					await SendAsync(envelope, cancellationToken);
				}
				finally
				{
					_pushesFlow.Release();
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// the connection ended
		}
		catch (Exception ex)
		{
			_logger?.LogDebug(ex, "Synqra replication: pushing to a client failed (the client is likely gone); its pushes stop.");
		}
		finally
		{
			_pushes.Writer.TryComplete();
		}
	}

	/// <summary>No more pushes: the pump ends once it has written what is queued.</summary>
	public void Complete() => _pushes.Writer.TryComplete();

	sealed class PushHold(SemaphoreSlim pushesFlow) : IDisposable
	{
		int _released;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _released, 1) == 0)
			{
				pushesFlow.Release();
			}
		}
	}
}
