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
#if NET10_0_OR_GREATER
using Synqra.Projection.Sqlite;
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
	IObjectStore __storeContext;

	public IObjectStore StoreContext { get =>
#if NETFRAMEWORK
			throw new NotImplementedException();
#else
			__storeContext ??= Host.Services.GetRequiredService<IObjectStore>();
#endif
	}

	public ushort Port { get; set; }

	SemaphoreSlim _semaphoreSlim = new(1, 1);
	long _masterSeq = 0;

	public SynqraTestNode(Action<IHostApplicationBuilder>? configureHost = null, bool masterHost = false)
	{
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
		builder.Services.AddTransient(typeof(Lazy<>), typeof(Lazier<>));
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
		var port = Port = GetNextAvailablePort();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Storage:JsonLinesStorage:FileName"] = Path.Combine(synqraTestsCurrentPath, "[TypeName].jsonl"),
			["URLS"] = "http://*:" + port,
		});

		builder.AddInMemorySynqraStore();
		builder.AddAppendStorageJsonLines<Event, Guid>();

		// builder.Services.AddSingleton<INetworkSerializationService, JsonNetworkSerializationService>();
		builder.Services.AddSingleton<INetworkSerializationService, SbxNetworkSerializationService>();

		builder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default);

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

		builder.AddTypeMetadataProvider([
			typeof(DemoModel),
			typeof(MyPocoTask),
		]);
		builder.Services.AddEmergencyLogger();

		builder.Services.AddSingleton<ISBXSerializerFactory>(new SBXSerializerFactory(() =>
		{
			var ser = new SBXSerializer();
			ser.Map(100, typeof(SamplePublicModel));
			ser.Map(101, typeof(SampleTaskModel));
			return ser;
		}));

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

		builder.Services.AddTransient(typeof(Lazy<>), typeof(Lazier<>));

		if (masterHost)
		{
			builder.Services.AddControllers();
		}
		else
		{
			builder.Services.AddSingleton<EventReplicationService>();
			builder.Services.AddSingleton<IEventReplicationService>(x => x.GetRequiredService<EventReplicationService>());
			builder.Services.AddHostedService(x => x.GetRequiredService<EventReplicationService>());

			builder.Services.AddSingleton<EventReplicationState>();
			// builder.Services.Configure<EventReplicationConfig>(builder.Configuration.GetSection(nameof(EventReplicationConfig)));
			builder.Services.AddSingleton<EventReplicationConfig>(new DelegatedEventReplicationConfig(() => Port));

		}

		configureHost?.Invoke(builder);
		var app = builder.Build();
		Host = app;

		if (masterHost)
		{
			var knownEvents = new ConcurrentDictionary<Guid, object?>();

			app.MapControllers();
			app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
			ConcurrentBag<WebSocket> _sockets = new ConcurrentBag<WebSocket>();
			app.Map("/api/synqra/ws", async ctx =>
			{
				var networkSerializationService = app.Services.GetRequiredService<INetworkSerializationService>();
				if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
				using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

				// Simple command registry (no reflection)
				/*
				var handlers = new Dictionary<string, Func<JsonElement, ValueTask<JsonElement>>>
				{
					["sum"] = async args =>
					{
						// args is a JSON array: [a, b]
						var a = args[0].GetInt32();
						var b = args[1].GetInt32();
						// SerializeToElement avoids intermediate strings and is AOT-friendly for primitives/known types
						return JsonSerializer.SerializeToElement(a + b);
					},
				};
				*/

				#region HELLO - from client
				var helloBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
				if (helloBytes is null)
				{
					return;
				}
				if (helloBytes.Length != 8)
				{
					throw new Exception($"Protocol Negotiation Failed! Received {helloBytes.Length} bytes instead of 8.");
				}
				var magic = BitConverter.ToUInt64(helloBytes);
				if (magic != networkSerializationService.Magic)
				{
					throw new Exception($"Protocol Negotiation Failed! Received Magic {magic:X16} instead of {networkSerializationService.Magic:X16}.");
				}
				#endregion
				#region HELLO - to client
				var magicBytes = BitConverter.GetBytes(networkSerializationService.Magic);
				await socket.SendAsync(magicBytes, WebSocketMessageType.Binary, endOfMessage: true, ctx.RequestAborted);
				#endregion

				_sockets.Add(socket);

				try
				{
					while (!ctx.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
					{
						var messageBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
						if (messageBytes is null)
						{
							break;
						}
						// var json = Encoding.UTF8.GetString(messageBytes);
						// var operation = JsonSerializer.Deserialize<TransportOperation>(json, AppJsonContext.Default.Options);
						var operation = networkSerializationService.Deserialize<TransportOperation>(messageBytes);

						var storeCtx = app.Services.GetRequiredService<IProjection>();
						if (operation is NewEvent1 newEvent1)
						{
							if (!knownEvents.TryAdd(newEvent1.Event.EventId, null))
							{
								// already known, ignore
								continue;
							}
							EmergencyLog.Default.Message($"Master Received: {newEvent1.Event}{Environment.NewLine}{JsonSerializer.Serialize(newEvent1.Event, AppJsonContext.Default.Options)}");
							await _semaphoreSlim.WaitAsync(ctx.RequestAborted);
							try
							{
								var ev = newEvent1.Event;
								// ev.MasterSeq
								await ev.AcceptAsync(storeCtx, null);
								var storage = app.Services.GetRequiredService<IAppendStorage<Event, Guid>>();
								await storage.AppendAsync(ev);
								var buffer = ArrayPool<byte>.Shared.Rent(EventReplicationService.DefaultFrameSize);
								try
								{
									var payload = networkSerializationService.Serialize<TransportOperation>(new NewEvent1 { Event = ev }, buffer);
									foreach (var item in _sockets)
									{
										if (item != socket)
										{
											try
											{
												await item.SendAsync(payload, networkSerializationService.IsTextOrBinary ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ctx.RequestAborted);
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
						/*
						try
						{
							var invoke = JsonSerializer.Deserialize(messageBytes.Value, AppJsonContext.Default);
							if (invoke?.Kind != "invoke") continue;

							JsonElement resultEl;
							string? error = null;

							if (handlers.TryGetValue(invoke.Data.Method, out var handler))
							{
								resultEl = await handler(invoke.Data.Args);
							}
							else
							{
								error = $"Unknown method '{invoke.Data.Method}'";
								resultEl = default;
							}
						// var result = new Envelope<ResultMessage>("result", new ResultMessage(invoke.Data.Id, error is null ? resultEl : null, error));


						var payload = JsonSerializer.SerializeToUtf8Bytes(result, AppJsonContext.Default);
							await socket.SendAsync(payload, WebSocketMessageType.Text, true, ctx.RequestAborted);
						}
						catch (Exception ex)
						{
							// best-effort error reply with id=-1 when deserialization fails
							var err = new Envelope<ResultMessage>("result", new ResultMessage(-1, null, ex.GetType().Name));
							var payload = JsonSerializer.SerializeToUtf8Bytes(err, WsJson.Default.EnvelopeResultMessage);
							await socket.SendAsync(payload, WebSocketMessageType.Text, true, ctx.RequestAborted);
						}
						*/
					}
				}
				catch (Exception ex)
				{
					EmergencyLog.Default.Error("Master Loop Error", ex);
				}
			});
		}

		_ = Host.StartAsync();
#endif
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

	private static ushort GetNextAvailablePort()
	{
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		var port = checked((ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
		listener.Stop();
		return port;
	}

}
