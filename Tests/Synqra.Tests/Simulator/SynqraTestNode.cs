#if NETFRAMEWORK
#else
using Microsoft.AspNetCore.Builder;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Synqra.Tests.SampleModels;
using Synqra.Tests.Helpers;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
// using System.Text.Json;
// using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Text.Json.Serialization.Metadata;
using System.Net.WebSockets;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Data.Common;
using Synqra.BinarySerializer;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Projection.InMemory;
using Synqra.AppendStorage.JsonLines;
using Synqra.AppendStorage;
using Synqra.AppendStorage.InMemory;
#if NET10_0_OR_GREATER
using Synqra.Projection.Sqlite;
using Synqra.Replication.AspNetCore;
using Synqra.Projection.MongoDb;
using Synqra.AppendStorage.MongoDb;
#endif

namespace Synqra.Tests.Simulator;

internal class SynqraTestNode
{
	[ThreadStatic]
	static int _fceRecursionGuard;

	static SynqraTestNode()
	{
		// Log all first-chance exceptions (for diagnostics, not errors)
		AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
		{
			if (_fceRecursionGuard > 2)
			{
				return;
			}
			var pre = _fceRecursionGuard;
			_fceRecursionGuard = pre + 1;
			try
			{
				if (_fceRecursionGuard > 1)
				{
					EmergencyLog.Default.Message("[Warning] First Chance Exception StackOverflow prevention happened!!");
				}
				Exception? exceptionToStringException = null;
				try
				{
					e.Exception.ToString();
				}
				catch (Exception ex)
				{
					exceptionToStringException = ex;
				}
				if (exceptionToStringException != null)
				{
					EmergencyLog.Default.Message("[Warning] First Chance Exception (Exception.ToString Exception!): " + exceptionToStringException);
				}
				else
				{
					EmergencyLog.Default.Message("[Warning] First Chance Exception: " + e.Exception);
				}
			}
			catch
			{
			}
			finally
			{
				_fceRecursionGuard = pre;
			}
		};
	}

#if NETFRAMEWORK
	public IHost Host { get; private set; }
#else
	public Microsoft.AspNetCore.Builder.WebApplication Host { get; private set; }
#endif

	/// <summary>
	/// The stream this node projects/replicates. All nodes in one test (master + clients) share the
	/// same stream id — it is passed in at construction, never pinned into a DI registration. The
	/// node OWNS its projection for this stream (obtained at startup from <see cref="IProjectionProvider"/>),
	/// which is why there is no DI-resolvable projection singleton anymore.
	/// </summary>
	public Guid StreamId { get; }

	// The node's own projection for StreamId, resolved once at startup via the provider (cached, so
	// every consumer here — StoreContext, the master WS apply loop, the client EventsReceived
	// catch-up — shares the one instance).
	private IReplayProjection? _projection;

	public IObjectStore StoreContext =>
#if NETFRAMEWORK
		throw new NotImplementedException();
#else
		_masterStore ?? _projection ?? throw new InvalidOperationException("Projection not initialized yet — await node.Started first.");
#endif

	public IEventLog Events
	{
		get =>
#if NETFRAMEWORK
			throw new NotImplementedException();
#else
			field ??= Host.Services.GetRequiredService<IEventLog>(); private set;
#endif
	}

	public ushort Port { get; set; }

	/// <summary>A client's HELLO kind; the default subscribes to its own stream as the server's default.</summary>
	public ReplicationHelloKind HelloKind { get; init; } = ReplicationHelloKind.UserDefaultMainStream;

	/// <summary>Whether a cold client asks for its stream's state (it does with a <see cref="ReplicationHelloKind.SubscribeTo"/> kind, which names the stream).</summary>
	public bool RequestSnapshot { get; init; } = true;

	/// <summary>A client's catch-up watchdog; null keeps the configured default.</summary>
	public TimeSpan? CatchUpTimeout { get; init; }

	/// <summary>Simulator master only: false makes it a master stuck in its catch-up, which reads a client's probe and never answers it.</summary>
	public bool AnswersCatchUp { get; set; } = true;

	/// <summary>Simulator master only: while true it answers every connect with 503, as a host that is down behind its proxy.</summary>
	public bool RefusesConnections { get; set; }

	int _connections;

	/// <summary>Simulator master only: the replication sockets it accepted.</summary>
	public int Connections => Volatile.Read(ref _connections);

	/// <summary>Simulator master only: the HELLO kind of each connection, in order (a 24-byte HELLO has none and is left out).</summary>
	public ConcurrentQueue<ReplicationHelloKind> HelloKinds { get; } = new ConcurrentQueue<ReplicationHelloKind>();

	/// <summary>Simulator master only: a pause before each backlog frame, as a catch-up that takes a while but keeps coming.</summary>
	public TimeSpan BacklogPause { get; set; }

	/// <summary>Simulator master only: a pause before its HELLO reply, as a master seeding or cutting a snapshot first.</summary>
	public TimeSpan HelloDelay { get; set; }

	/// <summary>A Mongo-backed master's store: one instance serves every stream, so a caller enters this node's stream around its use.</summary>
	IObjectStore? _masterStore;

	/// <summary>Scopes the calling flow to this node's stream, as a host's auth scopes a request: a Mongo-backed master's store reads it.</summary>
	public IDisposable EnterStream() => SynqraStreamContext.Enter(StreamId);

	/// <summary>
	/// Completes when the node's web host is listening. For the master node this is also
	/// the moment <see cref="Port"/> carries the real OS-assigned port — await it before
	/// constructing client nodes that target the master.
	/// </summary>
	public Task Started { get; private set; } = Task.CompletedTask;

	SemaphoreSlim _semaphoreSlim = new(1, 1);
	long _masterSeq = 0;

	/// <param name="useRealEndpoint">
	/// Master only (net10). When true the host wires the real production endpoint
	/// (<see cref="SynqraReplicationEndpointExtensions.MapSynqraReplicationEndpoint"/>) behind
	/// middleware that scopes each connection to the <c>?stream=</c> it connected with — the
	/// test's stand-in for Quotaly's auth middleware — instead of the hand-rolled single-stream
	/// simulator below. This is what the stream-isolation e2e exercises. The event log is then
	/// InMemory (it keeps <see cref="Event.StreamId"/> on the in-memory object; the JSON-lines log
	/// drops it, since StreamId is [JsonIgnore] and only the Mongo log re-maps it to _sid).
	/// </param>
	/// <param name="acknowledgeInbound">
	/// Simulator master only. False makes the master a dying one: it reads a client's event frames and does nothing
	/// with them, neither applying nor acknowledging, as the production endpoint did while its replay was broken.
	/// </param>
	/// <param name="mongoMaster">
	/// Master only (net10), with <paramref name="useRealEndpoint"/>: the production configuration — the log and the projection in
	/// the test Mongo (a database of its own per node), the projection serving snapshots. Its <see cref="StoreContext"/> is the
	/// multitenant Mongo store: enter <see cref="EnterStream"/> around its use.
	/// </param>
	/// <param name="serveSnapshots">In-memory master only, with <paramref name="useRealEndpoint"/>: its projections serve stream snapshots.</param>
	public SynqraTestNode(Guid streamId, Action<IHostApplicationBuilder>? configureHost = null, bool masterHost = false, bool useRealEndpoint = false, bool acknowledgeInbound = true, bool mongoMaster = false, bool serveSnapshots = false)
	{
		if (streamId == default)
		{
			throw new ArgumentException("A non-default stream id is required — all nodes in a test share one explicit stream.", nameof(streamId));
		}
		StreamId = streamId;
		#region Folder

		var utils = new TestUtils();
		var synqraTestsCurrentPath = utils.CreateTestFolder();
		Directory.CreateDirectory(synqraTestsCurrentPath);

		#endregion

#if NETFRAMEWORK
		throw new NotImplementedException();

		var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
		{
			ContentRootPath	= synqraTestsCurrentPath,
			EnvironmentName = Environments.Development,
		});
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Storage:JsonLinesStorage:FileName"] = Path.Combine(synqraTestsCurrentPath, "[TypeName].jsonl"),
		});
		builder.AddSynqraStoreContext();
		builder.AddJsonLinesStorage<Event, Guid>();
		builder.Services.AddSingleton<JsonSerializerContext>(TestJsonSerializerContext.Default);
		builder.Services.AddSingleton(TestJsonSerializerContext.Default.Options);
		// builder.Services.AddSignalR();
		builder.Services.AddLazyServiceResolution();
		if (masterHost)
		{
		}
		else
		{
			builder.Services.AddHostedService<EventReplicationService>(x => x.GetRequiredService<EventReplicationService>());
			builder.Services.AddSingleton<EventReplicationService>(); // x => x.GetServices<IHostedService>().OfType<EventReplicationService>().Single());
			// builder.Services.AddSingleton<EventReplicationService>(x => x.GetServices<IHostedService>().OfType<EventReplicationService>().Single());
			builder.Services.AddSingleton<EventReplicationState>();
			// services.Configure<EventReplicationConfig>(hostContext.Configuration.GetSection(nameof(EventReplicationConfig)));
			var port = Port = GetNextAvailablePort();
			builder.Services.Configure<EventReplicationConfig>(x =>
			{
				x.Port = port;
			});
		}
		Host = builder.Build();
#else
		var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
		{
			Args = Array.Empty<string>(),
			EnvironmentName = Environments.Development,
			ContentRootPath = synqraTestsCurrentPath,
		});
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Storage:JsonLinesStorage:FileName"] = Path.Combine(synqraTestsCurrentPath, "[TypeName].jsonl"),
			// Port 0: Kestrel asks the OS for a free port at bind time. Reserving a port
			// up front (listen, read, release, rebind) raced with parallel tests — the
			// released port could be taken again before Kestrel bound it.
			["URLS"] = "http://127.0.0.1:0",
		});

		builder.Services.AddInMemorySynqraStore();
#if NET10_0_OR_GREATER
		if (masterHost && useRealEndpoint && mongoMaster)
		{
			builder.Services.AddTestMongoDatabase(EphemeralMongo.ConnectionString).AddMongoEventStorage();
			builder.Services.AddMongoProjection(); // the projection a Quotaly master runs; it serves snapshots
		}
		else if (masterHost && useRealEndpoint)
		{
			// The real endpoint reads events back from its own log to replay a stream's backlog and
			// filters by Event.StreamId — so the log must preserve it. InMemory does (it holds the
			// object); JSON-lines does not ([JsonIgnore]). See the useRealEndpoint remarks above.
			builder.Services.AddAppendStorageInMemory<Event, (Guid, Guid)>(x => (x.StreamId, x.SequenceId));
			if (serveSnapshots)
			{
				builder.Services.AddInMemoryStreamSnapshots();
			}
		}
		else
#endif
		{
			builder.AddAppendStorageJsonLines<Event>("Key", x => (x.StreamId, x.SequenceId));
		}
		if (masterHost)
		{
			builder.Services.AddServerEventLog(); // the master owns commit order (docs/model.md §9)
		}
		else
		{
			builder.Services.AddClientEventLog(); // a node's own log is in arrival order
		}

		// builder.Services.AddSingleton<INetworkSerializationService, JsonNetworkSerializationService>();
		builder.Services.AddSingleton<INetworkSerializationService, SbxNetworkSerializationService>();

		// builder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default);
		builder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions);

		var options = new JsonSerializerOptions(SampleJsonSerializerContext.DefaultOptions);
		/*
		if (options.Converters.Count == 0)
		{
			Type[] extra = [
				typeof(SamplePublicModel),
				typeof(SampleTaskModel),
			];
			options.Converters.Add(new ObjectConverter(extra));
			// options.Converters.Add(new BindableModelConverter(extra));
			options.TypeInfoResolver = new SynqraJsonTypeInfoResolver(extra);
		}
		else
		{
			throw new Exception("Double check why we are here now");
		}
		*/
		/*
		var typeInfo = options.GetTypeInfo(typeof(IBindableModel));
		typeInfo.PolymorphismOptions ??= new JsonPolymorphismOptions
		{
			IgnoreUnrecognizedTypeDiscriminators = false,
			UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
			TypeDiscriminatorPropertyName = "_t",
		};
		typeInfo.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(SamplePublicModel), "SamplePublicModel"));
		typeInfo.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(SampleTaskModel), "SampleTaskModel"));
		*/
		builder.Services.AddSingleton(options);

		builder.Services.AddTypeMetadataProvider([
			typeof(DemoModel),
			typeof(MyPocoTask),
			typeof(SampleTaskModel),
		]);
		builder.Services.AddEmergencyLogger();

		builder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(100, typeof(SamplePublicModel));
			ser.Map(101, typeof(SampleTaskModel));
		});

		builder.Services.ConfigureHttpJsonOptions(o =>
		{
			o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
			o.SerializerOptions.Converters.Add(new ObjectConverter());
		});

		/*
		builder.Services.AddSignalR(o => o.EnableDetailedErrors = true)
			.AddJsonProtocol(o =>
			 {
				 // o.PayloadSerializerOptions = AppJsonContext.Default.Options;
				 // o.PayloadSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
				 o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
				 // Make the source-generated resolver first so it wins.
				 // o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
				 o.PayloadSerializerOptions.Converters.Add(new ObjectConverter());
				 // (Optional) tune other STJ options here if you really need to.
			 })
			;
		*/

		builder.Services.AddLazyServiceResolution();

		if (masterHost)
		{
			builder.Services.AddControllers();
#if NET10_0_OR_GREATER
			if (useRealEndpoint && !mongoMaster)
			{
				// The real endpoint resolves IProjection from DI and Accepts every inbound event.
				// This test only asserts socket-level routing, so a do-nothing projection suffices.
				builder.Services.AddSingleton<IProjection, FakeProjection>();
			}
#endif
		}
		else
		{
			builder.Services.AddSingleton<EventReplicationService>();
			builder.Services.AddSingleton<IEventReplicationService>(x => x.GetRequiredService<EventReplicationService>());
			builder.Services.AddHostedService(x => x.GetRequiredService<EventReplicationService>());

			builder.Services.AddSingleton<EventReplicationState>();
			// builder.Services.Configure<EventReplicationConfig>(builder.Configuration.GetSection(nameof(EventReplicationConfig)));
			// The client tells the master which stream it is on via the ?stream= query — the real
			// endpoint's scoping middleware reads it (the hand-rolled single-stream master below just
			// ignores it, matching on path only). Port is assigned after construction, so resolve both
			// lazily at connect time.
			builder.Services.AddSingleton<EventReplicationConfig>(new TestNodeReplicationConfig(this));

		}

		configureHost?.Invoke(builder);
		var app = builder.Build();
		Host = app;

#if NET10_0_OR_GREATER
		if (masterHost && useRealEndpoint)
		{
			app.MapControllers();
			// Stand-in for Quotaly's auth middleware: scope each connection to the stream it
			// connected with (?stream=<guid>). The real endpoint reads this ambient scope via
			// SynqraStreamContext.CurrentOrNull and never trusts anything the client sends on the wire.
			app.Use(next => async ctx =>
			{
				if (Guid.TryParse(ctx.Request.Query["stream"], out var connStream) && connStream != default)
				{
					using (SynqraStreamContext.Enter(connStream))
					{
						await next(ctx);
					}
				}
				else
				{
					await next(ctx);
				}
			});
			app.MapSynqraReplicationEndpoint();
		}
		else
#endif
		if (masterHost)
		{
			var knownEvents = new ConcurrentDictionary<Guid, object?>();

			app.MapControllers();
			app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
			app.Use(next => async ctx =>
			{
				if (RefusesConnections)
				{
					ctx.Response.StatusCode = 503;
				}
				else
				{
					await next(ctx);
				}
			});
			ConcurrentBag<WebSocket> _sockets = new ConcurrentBag<WebSocket>();
			app.Map("/api/synqra/ws", async ctx =>
			{
				var networkSerializationService = app.Services.GetRequiredService<INetworkSerializationService>();
				if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
				using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
				Interlocked.Increment(ref _connections);

				// Hand-rolled duplicate of SynqraReplicationEndpointExtensions.cs's HELLO/
				// broadcast protocol, kept in sync by hand — Synqra.Replication.AspNetCore
				// (the real production endpoint) is net10.0-only, but this test project also
				// builds for net8.0/net9.0, so it can't just call the real thing on every TFM.
				//
				// One intentional difference: the production endpoint additionally scopes backlog
				// replay and broadcast to each connection's own stream (ReplicationStreamScope).
				// This simulator is single-stream by construction (every node in a test shares one
				// StreamId, and there is no per-connection stream scope here), so that filter would
				// be a no-op — it is deliberately omitted rather than duplicated as dead code.

				#region HELLO - from client
				// 8 bytes magic + 16 bytes the client's own "last event id it already
				// received from a server" cursor, plus an optional 25th subscription-mode byte
				// (see EventReplicationService's HELLO send). This simulator is single-stream by
				// construction, so the mode byte is accepted and ignored — there is only ever one
				// stream to subscribe to.
				var helloBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
				if (helloBytes is null)
				{
					return;
				}
				if (helloBytes.Length is not (24 or 25 or 41))
				{
					throw new Exception($"Protocol Negotiation Failed! Received {helloBytes.Length} bytes instead of 24, 25 or 41.");
				}
				var magic = BitConverter.ToUInt64(helloBytes, 0);
				if (magic != networkSerializationService.Magic)
				{
					throw new Exception($"Protocol Negotiation Failed! Received Magic {magic:X16} instead of {networkSerializationService.Magic:X16}.");
				}
				var clientCursor = new Guid(helloBytes.AsSpan(8, 16));
				if (helloBytes.Length >= 25)
				{
					HelloKinds.Enqueue((ReplicationHelloKind)helloBytes[24]);
				}
				if (HelloDelay > TimeSpan.Zero)
				{
					await Task.Delay(HelloDelay, ctx.RequestAborted);
				}
				#endregion
				#region HELLO - to client, then replay this stream's backlog since clientCursor
				// Register for broadcasts BEFORE answering HELLO, and do both — plus the
				// backlog replay below — under the broadcast semaphore: the moment a client
				// observes the HELLO reply (and reports IsOnline), every subsequent broadcast
				// is guaranteed to reach it, and none of this can interleave with a concurrent
				// broadcast SendAsync on the same socket.
				var magicBytes = BitConverter.GetBytes(networkSerializationService.Magic);
				await _semaphoreSlim.WaitAsync(ctx.RequestAborted);
				try
				{
					_sockets.Add(socket);
					await socket.SendAsync(magicBytes, WebSocketMessageType.Binary, endOfMessage: true, ctx.RequestAborted);
					// The HELLO ack, as the real endpoint sends it: the client files received events under its one subscribed stream.
					var ack = networkSerializationService.Serialize<TransportOperation>(new SubscriptionState { Streams = [StreamId] }, new ArraySegment<byte>(new byte[EventReplicationService.DefaultFrameSize]));
					await socket.SendAsync(ack, networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ctx.RequestAborted);

					var backlogStorage = app.Services.GetRequiredService<IEventLog>();
					var backlogBuffer = ArrayPool<byte>.Shared.Rent(EventReplicationService.DefaultFrameSize);
					try
					{
						await foreach (var ev in backlogStorage.ReadStreamAsync(StreamId, clientCursor, ctx.RequestAborted))
						{
							knownEvents.TryAdd(ev.EventId, null);
							if (BacklogPause > TimeSpan.Zero)
							{
								await Task.Delay(BacklogPause, ctx.RequestAborted);
							}
							var payload = networkSerializationService.Serialize<TransportOperation>(new EventEnvelope { Event = ev }, new ArraySegment<byte>(backlogBuffer));
							await socket.SendAsync(payload, networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ctx.RequestAborted);
						}
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(backlogBuffer);
					}
				}
				finally
				{
					_semaphoreSlim.Release();
				}
				#endregion

				try
				{
					while (!ctx.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
					{
						var messageBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
						if (messageBytes is null)
						{
							break;
						}
						// Post-HELLO frames are SBX TransportOperations. This single-stream simulator only
						// acts on EventEnvelope; client-driven subscription control is accepted and ignored.
						var operation = networkSerializationService.Deserialize<TransportOperation>(messageBytes);
						if (operation is SubscribeRequest or UnsubscribeRequest)
						{
							continue;
						}
						if (operation is CatchUpRequest catchUp)
						{
							if (AnswersCatchUp)
							{
								// read after the backlog above, as the real endpoint does; under the gate, which every broadcast to this socket takes
								await _semaphoreSlim.WaitAsync(ctx.RequestAborted);
								try
								{
									await socket.SendOperationAsync(networkSerializationService, new CaughtUp { RequestId = catchUp.RequestId }, ctx.RequestAborted);
								}
								finally
								{
									_semaphoreSlim.Release();
								}
							}
							continue;
						}

						var storeCtx = _projection ?? throw new InvalidOperationException("Master projection not initialized.");
						if (operation is EventEnvelope eventEnvelope)
						{
							if (!acknowledgeInbound)
							{
								continue; // a dying master: the frame arrived and nothing came of it
							}
							if (!knownEvents.TryAdd(eventEnvelope.Event.EventId, null))
							{
								// already known: acknowledged again, as the real endpoint does, never re-applied
								await socket.SendOperationAsync(networkSerializationService, new EventAccepted { EventId = eventEnvelope.Event.EventId }, ctx.RequestAborted);
								continue;
							}
							EmergencyLog.Default.Message($"Master Received: {eventEnvelope.Event}{Environment.NewLine}{JsonSerializer.Serialize(eventEnvelope.Event, AppJsonContext.Default.Options)}");
							await _semaphoreSlim.WaitAsync(ctx.RequestAborted);
							try
							{
								var ev = eventEnvelope.Event;
								ev.StreamId = StreamId; // this master serves its own stream; the stream is not on the wire
								ev.SequenceId = default; // the client's local stamp: this log stamps its own
								await ev.AcceptAsync(storeCtx, null);
								var storage = app.Services.GetRequiredService<IEventLog>();
								await storage.AppendAsync(ev);
								await socket.SendOperationAsync(networkSerializationService, new EventAccepted { EventId = ev.EventId }, ctx.RequestAborted); // the sender's cursor moves on this
								var buffer = ArrayPool<byte>.Shared.Rent(EventReplicationService.DefaultFrameSize);
								try
								{
									var payload = networkSerializationService.Serialize<TransportOperation>(new EventEnvelope { Event = ev }, new ArraySegment<byte>(buffer));
									var taggedFrame = payload;
									foreach (var item in _sockets)
									{
										if (item != socket)
										{
											try
											{
												await item.SendAsync(taggedFrame, networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ctx.RequestAborted);
											}
											catch
											{
												// ignore
											}
										}
									}
								}
								finally
								{
									ArrayPool<byte>.Shared.Return(buffer);
								}
							}
							finally
							{
								_semaphoreSlim.Release();
							}
						}
						else
						{
							throw new NotSupportedException();
						}
					}
				}
				catch (Exception ex)
				{
					EmergencyLog.Default.Error("Master Loop Error", ex);
				}
			});
		}

		Started = StartHostAsync(app, masterHost, mongoMaster);
#endif
	}

#if !NETFRAMEWORK
	async Task StartHostAsync(Microsoft.AspNetCore.Builder.WebApplication app, bool masterHost, bool mongoMaster)
	{
		await app.StartAsync();
		if (masterHost)
		{
			// Kestrel bound to 127.0.0.1:0 — publish the OS-assigned port so clients can
			// target it. Client nodes keep the master port assigned by the test instead.
			Port = checked((ushort)new Uri(app.Urls.First()).Port);
		}

		// Own the projection for this node's stream. The provider caches per stream and brings it to
		// head via the keeper, so this single instance is what StoreContext, the master apply loop,
		// and the client catch-up below all share — no DI-registered projection singleton.
		var provider = app.Services.GetRequiredService<IProjectionProvider>();
		if (mongoMaster)
		{
			_masterStore = app.Services.GetRequiredService<IObjectStore>(); // the Mongo store the endpoint applies peers' events to
		}
		else
		{
			_projection = await provider.GetAsync(StreamId);
		}

		if (!masterHost)
		{
			// Transport-only replication service: when it appends incoming server events to local
			// durable storage it raises EventsReceived; fold them into this node's projection via the
			// keeper (cheap no-op if nothing new for this stream). Tests poll for the result, so a
			// fire-and-forget catch-up is fine; the provider serializes concurrent catch-ups.
			var ers = app.Services.GetRequiredService<IEventReplicationService>();
			ers.EventsReceived += () => _ = provider.GetAsync(StreamId);
		}
	}
#endif

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

	private static ushort GetNextAvailablePort()
	{
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		var port = checked((ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
		listener.Stop();
		return port;
	}

	/// <summary>
	/// Replication config for a client test node. The master's port is only known after this node is
	/// constructed (the test assigns <see cref="Port"/> via an object initializer), so both the port
	/// and the full endpoint are resolved lazily at connect time. The endpoint carries this node's
	/// own <see cref="StreamId"/> as <c>?stream=</c> so a real-endpoint master can scope the
	/// connection; the hand-rolled single-stream master ignores it (it matches on path only).
	/// </summary>
	private sealed class TestNodeReplicationConfig : EventReplicationConfig
	{
		private readonly SynqraTestNode _node;
		public TestNodeReplicationConfig(SynqraTestNode node) => _node = node;
		public override ushort Port => _node.Port;
		public override ReplicationHelloKind HelloKind => _node.HelloKind;
		public override Guid? InitialSubscribeStreamId => _node.HelloKind is ReplicationHelloKind.SubscribeTo or ReplicationHelloKind.SubscribeToSnapshot ? _node.StreamId : null;
		public override bool RequestSnapshot => _node.RequestSnapshot;
		public override TimeSpan CatchUpTimeout => _node.CatchUpTimeout ?? base.CatchUpTimeout;
		// The master binds 127.0.0.1 only: "localhost" tries ::1 first, and on Windows each refused attempt costs about 2 s.
		public override string? Endpoint => $"ws://127.0.0.1:{_node.Port}/api/synqra/ws?stream={_node.StreamId:D}";
	}

}
