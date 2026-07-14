using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.AppendStorage.InMemory;
using Synqra.AppendStorage.JsonLines;
using Synqra.BinarySerializer;
using Synqra.BlobStorage.Sqlite;
using Synqra.Projection.InMemory;
using Synqra.Tests.SampleModels;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.TestHelpers;
#if NET10_0_OR_GREATER
using Synqra.Replication.AspNetCore;
#endif

namespace Synqra.Tests.Simulator;

internal sealed class SynqraTestNode
{
	private readonly SemaphoreSlim _transportGate = new(1, 1);
	private IReplayProjection? _projection;

	public SynqraTestNode(
		  Guid streamId
		, Action<IHostApplicationBuilder>? configureHost = null
		, bool masterHost = false
		, bool useRealEndpoint = false
	)
	{
		if (streamId == Guid.Empty)
		{
			throw new ArgumentException("A non-default stream id is required.", nameof(streamId));
		}

		StreamId = streamId;
		var workingDirectory = new TestUtils().CreateTestFolder();
		Directory.CreateDirectory(workingDirectory);

		var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
		{
			Args = Array.Empty<string>(),
			EnvironmentName = Environments.Development,
			ContentRootPath = workingDirectory,
		});
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Storage:JsonLinesStorage:FileName"] = Path.Combine(workingDirectory, "[TypeName].jsonl"),
			["Storage:BlobStorage:Sqlite:ConnectionString"] = $"Data Source={Path.Combine(workingDirectory, "client.db")}",
			["URLS"] = "http://127.0.0.1:0",
		});

		builder.Services.AddInMemorySynqraStore();
		if (masterHost)
		{
#if NET10_0_OR_GREATER
			if (useRealEndpoint)
			{
				builder.Services.AddAppendStorageInMemory<Event, Guid>(x => x.EventId);
			}
			else
#endif
			{
			builder.AddAppendStorageJsonLines<Event>("EventId", x => x.EventId);
			}
			builder.Services.AddReplicationProtocol();
			builder.Services.AddControllers();
		}
		else
		{
			builder.AddBlobStorageSqlite<Guid>("confirmed-events");
			builder.AddBlobStorageSqlite<Guid>("pending-command-batches");
			builder.Services.AddClientEventStoreBlob("confirmed-events", "pending-command-batches");
			builder.Services.AddSingleton<EventReplicationService>();
			builder.Services.AddSingleton<IEventReplicationService>(services =>
				services.GetRequiredService<EventReplicationService>());
			builder.Services.AddHostedService(services =>
				services.GetRequiredService<EventReplicationService>());
			builder.Services.AddSingleton<EventReplicationConfig>(new TestNodeReplicationConfig(this));
		}

		builder.Services.AddSingleton<INetworkSerializationService, SbxNetworkSerializationService>();
		builder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions);
		builder.Services.AddSingleton(new JsonSerializerOptions(SampleJsonSerializerContext.DefaultOptions));
		builder.Services.AddTypeMetadataProvider([
			typeof(DemoModel),
			typeof(MyPocoTask),
			typeof(SampleTaskModel),
		]);
		builder.Services.AddEmergencyLogger();
		builder.Services.AddSbxSerializer(serializer =>
		{
			serializer.Map(100, typeof(SamplePublicModel));
			serializer.Map(101, typeof(SampleTaskModel));
		});
		builder.Services.ConfigureHttpJsonOptions(options =>
		{
			options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
			options.SerializerOptions.Converters.Add(new ObjectConverter());
		});
		builder.Services.AddLazyServiceResolution();

		configureHost?.Invoke(builder);
		Host = builder.Build();
		if (masterHost)
		{
#if NET10_0_OR_GREATER
			if (useRealEndpoint)
			{
				Host.MapControllers();
				Host.Use(next => async context =>
				{
					if (Guid.TryParse(context.Request.Query["stream"], out var streamId)
						&& streamId != Guid.Empty
					)
					{
						using (SynqraStreamContext.Enter(streamId))
						{
							await next(context);
						}
						return;
					}
					await next(context);
				});
				Host.MapSynqraReplicationEndpoint();
			}
			else
#endif
			{
			MapReplicationEndpoint(Host);
			}
		}
		Started = StartHostAsync(Host, masterHost);
	}

	public WebApplication Host { get; }
	public Guid StreamId { get; }
	public ushort Port { get; set; }
	public Task Started { get; }

	public IObjectStore StoreContext => _projection
		?? throw new InvalidOperationException("Projection not initialized; await Started first.");

	public IAppendStorage<Event, Guid> Events =>
		Host.Services.GetRequiredService<IAppendStorage<Event, Guid>>();

	private void MapReplicationEndpoint(WebApplication app)
	{
		var sockets = new ConcurrentDictionary<WebSocket, byte>();
		app.MapControllers();
		app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
		app.Map("/api/synqra/ws", async context =>
		{
			if (!context.WebSockets.IsWebSocketRequest)
			{
				context.Response.StatusCode = 400;
				return;
			}

			var protocol = context.RequestServices.GetRequiredService<ReplicationProtocol>();
			var serializer = context.RequestServices.GetRequiredService<INetworkSerializationService>();
			using var socket = await context.WebSockets.AcceptWebSocketAsync();
			try
			{
				var helloFrame = await ReceiveFullMessageAsync(socket, context.RequestAborted)
					?? throw new InvalidOperationException("Client closed before sending replication hello.");
				var hello = protocol.ReadHello(helloFrame);
				if (hello.Magic != serializer.Magic)
				{
					throw new InvalidDataException("Replication serializer does not match the server.");
				}

				await _transportGate.WaitAsync(context.RequestAborted);
				try
				{
					sockets[socket] = 0;
					await SendAsync(socket, protocol.CreateHelloAccepted(serializer.Magic), context.RequestAborted);
					await RepairConfirmedEventsAsync(
						socket,
						hello.ConfirmedEvents,
						protocol,
						context.RequestAborted);
				}
				finally
				{
					_transportGate.Release();
				}

				while (!context.RequestAborted.IsCancellationRequested
					&& socket.State == WebSocketState.Open)
				{
					var frame = await ReceiveFullMessageAsync(socket, context.RequestAborted);
					if (frame is null)
					{
						break;
					}
					if (protocol.ReadKind(frame) != ReplicationFrameKind.SubmitCommand)
					{
						throw new InvalidDataException("The test server accepts only command submissions after repair.");
					}
					await ProcessCommandAsync(
						socket,
						sockets,
						protocol.ReadSubmittedCommand(frame),
						protocol,
						context.RequestAborted);
				}
			}
			finally
			{
				sockets.TryRemove(socket, out _);
			}
		});
	}

	private async Task RepairConfirmedEventsAsync(
		  WebSocket socket
		, IReadOnlyDictionary<Guid, uint> clientDigests
		, ReplicationProtocol protocol
		, CancellationToken cancellationToken
	)
	{
		var unmatched = clientDigests.ToDictionary();
		await foreach (var ev in Events.GetAllAsync(cancellationToken: cancellationToken))
		{
			if (ev.StreamId != Guid.Empty && ev.StreamId != StreamId)
			{
				continue;
			}
			if (!unmatched.Remove(ev.EventId, out var clientHash)
				|| clientHash != protocol.HashEvent(ev))
			{
				await SendAsync(socket, protocol.CreateConfirmedEvent(ev), cancellationToken);
			}
		}
		foreach (var eventId in unmatched.Keys)
		{
			await SendAsync(socket, protocol.CreateDeleteConfirmedEvent(eventId), cancellationToken);
		}
		await SendAsync(socket, protocol.CreateRepairComplete(), cancellationToken);
	}

	private async Task ProcessCommandAsync(
		  WebSocket origin
		, ConcurrentDictionary<WebSocket, byte> sockets
		, SubmittedCommand submitted
		, ReplicationProtocol protocol
		, CancellationToken cancellationToken
	)
	{
		submitted.Command.StreamId = StreamId;
		await _transportGate.WaitAsync(cancellationToken);
		try
		{
			var events = await ReadCommandEventsAsync(submitted.Command.CommandId, cancellationToken);
			var wasAlreadyProcessed = events.Count > 0;
			if (!wasAlreadyProcessed)
			{
				var provider = Host.Services.GetRequiredService<IProjectionProvider>();
				var objectStore = (IObjectStore)await provider.GetAsync(StreamId, cancellationToken);
				await objectStore.SubmitCommandAsync(submitted.Command, submitted.Options);
				events = await ReadCommandEventsAsync(submitted.Command.CommandId, cancellationToken);
			}

			foreach (var ev in events)
			{
				ev.StreamId = StreamId;
				var eventFrame = protocol.CreateConfirmedEvent(ev);
				foreach (var peer in sockets.Keys)
				{
					if (peer.State != WebSocketState.Open
						|| wasAlreadyProcessed && peer != origin)
					{
						continue;
					}
					try
					{
						await SendAsync(peer, eventFrame, cancellationToken);
					}
					catch (WebSocketException)
					{
						sockets.TryRemove(peer, out _);
					}
				}
			}
			await SendAsync(
				origin,
				protocol.CreateCommandAcknowledged(submitted.Command.CommandId),
				cancellationToken);
		}
		catch (Exception ex)
		{
			if (origin.State == WebSocketState.Open)
			{
				await SendAsync(
					origin,
					protocol.CreateCommandRejected(submitted.Command.CommandId, ex.Message),
					cancellationToken);
			}
		}
		finally
		{
			_transportGate.Release();
		}
	}

	private async Task<List<Event>> ReadCommandEventsAsync(
		  Guid commandId
		, CancellationToken cancellationToken
	)
	{
		var result = new List<Event>();
		await foreach (var ev in Events.GetAllAsync(cancellationToken: cancellationToken))
		{
			if (ev.CommandId == commandId
				&& (ev.StreamId == Guid.Empty || ev.StreamId == StreamId))
			{
				result.Add(ev);
			}
		}
		return result;
	}

	private async Task StartHostAsync(WebApplication app, bool masterHost)
	{
		await app.StartAsync();
		if (masterHost)
		{
			Port = checked((ushort)new Uri(app.Urls.First()).Port);
		}

		var provider = app.Services.GetRequiredService<IProjectionProvider>();
		if (!masterHost)
		{
			var replication = app.Services.GetRequiredService<IEventReplicationService>();
			replication.EventsReceived += () => _ = MaintainProjectionAsync(provider);
			replication.RebuildRequired += () => _ = RebuildProjectionAsync(provider);
		}
		_projection = await provider.GetAsync(StreamId);
	}

	private async Task MaintainProjectionAsync(IProjectionProvider provider)
	{
		_projection = await provider.GetAsync(StreamId);
	}

	private async Task RebuildProjectionAsync(IProjectionProvider provider)
	{
		_projection = await provider.RebuildAsync(StreamId);
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
				stream.Write(buffer, 0, result.Count);
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

	private static Task SendAsync(
		  WebSocket socket
		, byte[] frame
		, CancellationToken cancellationToken
	)
	{
		return socket.SendAsync(
			frame,
			WebSocketMessageType.Binary,
			endOfMessage: true,
			cancellationToken);
	}

	private sealed class TestNodeReplicationConfig : EventReplicationConfig
	{
		private readonly SynqraTestNode _node;

		public TestNodeReplicationConfig(SynqraTestNode node)
		{
			_node = node;
			ResolveStreamIdAsync = _ => Task.FromResult<Guid?>(_node.StreamId);
		}

		public override ushort Port => _node.Port;
		public override string? Endpoint =>
			$"ws://localhost:{_node.Port}/api/synqra/ws?stream={_node.StreamId:D}";
	}
}
