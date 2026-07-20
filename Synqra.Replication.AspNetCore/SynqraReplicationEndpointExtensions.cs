using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synqra.AppendStorage;

namespace Synqra.Replication.AspNetCore;

public static class SynqraReplicationEndpointExtensions
{
	public static WebApplication MapSynqraReplicationEndpoint(
		  this WebApplication app
		, string path = "/api/synqra/ws"
		, string? serviceKey = null
	)
	{
		app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
		var sockets = new ConcurrentDictionary<WebSocket, Guid?>();
		var transportGate = new SemaphoreSlim(1, 1);

		app.Map(path, async context =>
		{
			var logger = context.RequestServices
				.GetService<ILoggerFactory>()?
				.CreateLogger("Synqra.Replication.Endpoint");
			if (!context.WebSockets.IsWebSocketRequest)
			{
				context.Response.StatusCode = 400;
				return;
			}

			var protocol = context.RequestServices.GetRequiredService<ReplicationProtocol>();
			var networkSerializationService = context.RequestServices.GetRequiredService<INetworkSerializationService>();
			using var socket = await context.WebSockets.AcceptWebSocketAsync();
			var connectionStream = SynqraStreamContext.CurrentOrNull;

			try
			{
				var helloFrame = await ReceiveFullMessageAsync(socket, context.RequestAborted)
					?? throw new InvalidOperationException("Client closed before sending replication hello.");
				var hello = protocol.ReadHello(helloFrame);
				if (hello.Magic != networkSerializationService.Magic)
				{
					throw new InvalidOperationException(
						$"Replication magic {hello.Magic:X16} does not match {networkSerializationService.Magic:X16}."
					);
				}

				await transportGate.WaitAsync(context.RequestAborted);
				try
				{
					sockets[socket] = connectionStream;
					await SendAsync(
						socket,
						protocol.CreateHelloAccepted(networkSerializationService.Magic),
						context.RequestAborted
					);
					await RepairConfirmedEventsAsync(
						  context.RequestServices
						, socket
						, connectionStream
						, hello.ConfirmedEvents
						, protocol
						, serviceKey
						, context.RequestAborted
					);
				}
				finally
				{
					transportGate.Release();
				}

				while (!context.RequestAborted.IsCancellationRequested
					&& socket.State == WebSocketState.Open
				)
				{
					var frame = await ReceiveFullMessageAsync(socket, context.RequestAborted);
					if (frame is null)
					{
						break;
					}
					if (protocol.ReadKind(frame) != ReplicationFrameKind.SubmitCommand)
					{
						throw new InvalidDataException("The server accepts only command submissions after repair.");
					}

					var submitted = protocol.ReadSubmittedCommand(frame);
					if (connectionStream is Guid streamId)
					{
						submitted.Command.StreamId = streamId;
					}

					await transportGate.WaitAsync(context.RequestAborted);
					try
					{
						var storage = ResolveStorage(context.RequestServices, serviceKey);
						var events = await ReadCommandEventsAsync(
							storage,
							submitted.Command.CommandId,
							connectionStream,
							context.RequestAborted
						);
						var wasAlreadyProcessed = events.Count > 0;
						if (!wasAlreadyProcessed)
						{
							var objectStore = await ResolveObjectStoreAsync(
								  context.RequestServices
								, serviceKey
								, connectionStream
								, context.RequestAborted
							);
							await objectStore.SubmitCommandAsync(submitted.Command, submitted.Options);
							events = await ReadCommandEventsAsync(
								storage,
								submitted.Command.CommandId,
								connectionStream,
								context.RequestAborted
							);
						}

						foreach (var ev in events)
						{
							if (connectionStream is Guid eventStream)
							{
								ev.StreamId = eventStream;
							}
							var eventFrame = protocol.CreateConfirmedEvent(ev);
							foreach (var (peer, peerStream) in sockets)
							{
								if (peer.State != WebSocketState.Open
									|| wasAlreadyProcessed && peer != socket
									|| !ReplicationStreamScope.Admits(peerStream, connectionStream)
								)
								{
									continue;
								}
								await SendAsync(peer, eventFrame, context.RequestAborted);
							}
						}
						await SendAsync(
							socket,
							protocol.CreateCommandAcknowledged(submitted.Command.CommandId),
							context.RequestAborted
						);
					}
					catch (Exception ex)
					{
						logger?.LogWarning(
							ex,
							"Synqra replication rejected command {CommandId} on stream {Stream}.",
							submitted.Command.CommandId,
							connectionStream
						);
						if (socket.State == WebSocketState.Open)
						{
							await SendAsync(
								socket,
								protocol.CreateCommandRejected(submitted.Command.CommandId, ex.Message),
								context.RequestAborted
							);
						}
					}
					finally
					{
						transportGate.Release();
					}
				}
			}
			finally
			{
				sockets.TryRemove(socket, out _);
			}
		});

		return app;
	}

	private static async Task RepairConfirmedEventsAsync(
		  IServiceProvider serviceProvider
		, WebSocket socket
		, Guid? connectionStream
		, IReadOnlyDictionary<Guid, uint> clientDigests
		, ReplicationProtocol protocol
		, string? serviceKey
		, CancellationToken cancellationToken
	)
	{
		var remainingClientEvents = clientDigests.ToDictionary();
		var storage = ResolveStorage(serviceProvider, serviceKey);
		await foreach (var ev in storage.GetAllAsync(cancellationToken: cancellationToken))
		{
			if (!ReplicationStreamScope.Admits(ev.StreamId, connectionStream))
			{
				continue;
			}
			if (!remainingClientEvents.Remove(ev.EventId, out var clientHash)
				|| clientHash != protocol.HashEvent(ev)
			)
			{
				await SendAsync(socket, protocol.CreateConfirmedEvent(ev), cancellationToken);
			}
		}
		foreach (var eventId in remainingClientEvents.Keys)
		{
			await SendAsync(socket, protocol.CreateDeleteConfirmedEvent(eventId), cancellationToken);
		}
		await SendAsync(socket, protocol.CreateRepairComplete(), cancellationToken);
	}

	private static async Task<List<Event>> ReadCommandEventsAsync(
		  IAppendStorage<Event, Guid> storage
		, Guid commandId
		, Guid? connectionStream
		, CancellationToken cancellationToken
	)
	{
		var result = new List<Event>();
		await foreach (var ev in storage.GetAllAsync(cancellationToken: cancellationToken))
		{
			if (ev.CommandId == commandId
				&& ReplicationStreamScope.Admits(ev.StreamId, connectionStream)
			)
			{
				result.Add(ev);
			}
		}
		return result;
	}

	private static IAppendStorage<Event, Guid> ResolveStorage(
		  IServiceProvider serviceProvider
		, string? serviceKey
	)
	{
		return serviceKey is null
			? serviceProvider.GetRequiredService<IAppendStorage<Event, Guid>>()
			: serviceProvider.GetRequiredKeyedService<IAppendStorage<Event, Guid>>(serviceKey);
	}

	private static async Task<IObjectStore> ResolveObjectStoreAsync(
		  IServiceProvider serviceProvider
		, string? serviceKey
		, Guid? streamId
		, CancellationToken cancellationToken
	)
	{
		var store = serviceKey is null
			? serviceProvider.GetService<IObjectStore>()
			: serviceProvider.GetKeyedService<IObjectStore>(serviceKey);
		if (store is not null)
		{
			return store;
		}
		if (streamId is not Guid stream || stream == Guid.Empty)
		{
			throw new InvalidOperationException(
				"Replication requires an object store, or a projection provider and an authenticated stream."
			);
		}

		var provider = serviceKey is null
			? serviceProvider.GetService<IProjectionProvider>()
			: serviceProvider.GetKeyedService<IProjectionProvider>(serviceKey);
		if (provider is null)
		{
			throw new InvalidOperationException("No object store or projection provider is registered for replication.");
		}
		return (IObjectStore)await provider.GetAsync(stream, cancellationToken);
	}

	private static async Task<byte[]?> ReceiveFullMessageAsync(
		  WebSocket socket
		, CancellationToken cancellationToken
	)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(EventReplicationService.DefaultFrameSize);
		try
		{
			using var stream = new MemoryStream(EventReplicationService.DefaultFrameSize);
			while (!cancellationToken.IsCancellationRequested)
			{
				var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
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

	private static async Task SendAsync(
		  WebSocket socket
		, byte[] frame
		, CancellationToken cancellationToken
	)
	{
		await socket.SendAsync(
			frame,
			WebSocketMessageType.Binary,
			endOfMessage: true,
			cancellationToken
		);
	}
}
