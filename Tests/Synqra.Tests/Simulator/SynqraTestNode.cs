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
using Synqra.Storage;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.Helpers;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Text.Json.Serialization.Metadata;
using System.Net.WebSockets;
using System.Buffers;
using System.Collections.Concurrent;

namespace Synqra.Tests.Simulator;

internal class SynqraTestNode
{
	static SynqraTestNode()
	{
		AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
		{
			EmergencyLog.Default.Message($"{e.Exception}");
		};
	}

#if NETFRAMEWORK
	public IHost Host { get; private set; }
#else
	public Microsoft.AspNetCore.Builder.WebApplication Host { get; private set; }
#endif
	ISynqraStoreContext __storeContext;

	public ISynqraStoreContext StoreContext { get =>
#if NETFRAMEWORK
			throw new NotImplementedException();
#else
			__storeContext ??= Host.Services.GetRequiredService<ISynqraStoreContext>();
#endif
	}

	public ushort Port { get; set; }

	SemaphoreSlim _semaphoreSlim = new(1, 1);

	public SynqraTestNode(Action<IHostApplicationBuilder>? configureHost = null, bool masterHost = false)
	{
		#region Folder

		var utils = new TestUtils();
		var synqraTestsCurrentPath = utils.CreateTestFolder();

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
			["JsonLinesStorage:FileName"] = Path.Combine(synqraTestsCurrentPath, "[TypeName].jsonl"),
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
			["JsonLinesStorage:FileName"] = Path.Combine(synqraTestsCurrentPath, "[TypeName].jsonl"),
			["URLS"] = "http://*:" + port,
		});

		builder.AddSynqraStoreContext();
		builder.AddJsonLinesStorage<Event, Guid>();
		builder.Services.AddSingleton<JsonSerializerContext>(TestJsonSerializerContext.Default);
		var options = new JsonSerializerOptions(TestJsonSerializerContext.Default.Options);
		options.Converters.Add(new ObjectConverter());
		builder.Services.AddSingleton(options);

		builder.Services.ConfigureHttpJsonOptions(o =>
		{
			o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
			o.SerializerOptions.Converters.Add(new ObjectConverter());
		});

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

		builder.Services.AddTransient(typeof(Lazy<>), typeof(Lazier<>));

		if (masterHost)
		{
			builder.Services.AddControllers();
		}
		else
		{
			builder.Services.AddHostedService<EventReplicationService>(x => x.GetRequiredService<EventReplicationService>());

			builder.Services.AddSingleton<EventReplicationService>(); // x => x.GetServices<IHostedService>().OfType<EventReplicationService>().Single());
			// builder.Services.AddSingleton<EventReplicationService>(x => x.GetServices<IHostedService>().OfType<EventReplicationService>().Single());

			builder.Services.AddSingleton<EventReplicationState>();
			// builder.Services.Configure<EventReplicationConfig>(builder.Configuration.GetSection(nameof(EventReplicationConfig)));
			builder.Services.AddSingleton<EventReplicationConfig>(new DelegatedEventReplicationConfig(() => Port));

		}

		configureHost?.Invoke(builder);
		var app = builder.Build();
		Host = app;

		if (masterHost)
		{
			app.MapControllers();
			app.MapHub<SynqraSignalerHub>("/api/synqra/signalR");
			app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
			ConcurrentBag<WebSocket> _sockets = new ConcurrentBag<WebSocket>();
			app.Map("/api/synqra/ws", async ctx =>
			{
				if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
				using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

				_sockets.Add(socket);
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

				while (!ctx.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
				{
					var messageBytes = await ReceiveFullMessageAsync(socket, ctx.RequestAborted);
					if (messageBytes is null)
					{
						break;
					}
					var json = Encoding.UTF8.GetString(messageBytes);
					var operation = JsonSerializer.Deserialize<TransportOperation>(json, AppJsonContext.Default.Options);
					var storeCtx = app.Services.GetRequiredService<ISynqraStoreContext>();
					if (operation is NewEvent1 newEvent1)
					{
						await _semaphoreSlim.WaitAsync(ctx.RequestAborted);
						try
						{
							var ev = newEvent1.Event;
							await ev.AcceptAsync(storeCtx, null);
							var storage = app.Services.GetRequiredService<IStorage<Event, Guid>>();
							await storage.AppendAsync(ev);
							foreach (var item in _sockets)
							{
								if (item != socket)
								{
									// var payload = JsonSerializer.SerializeToUtf8Bytes<TransportOperation>(new Envelope<EventMessage>("event", new EventMessage(ev)), AppJsonContext.Default);
									await item.SendAsync(messageBytes, WebSocketMessageType.Text, true, ctx.RequestAborted);
								}
							}
						}
						finally
						{
							_semaphoreSlim.Release();
						}
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

	private class Lazier<
#if !NETFRAMEWORK
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
#endif
		T> : Lazy<T>
		where T : class
	{
		public Lazier(IServiceProvider serviceProvider)
			: base(() => serviceProvider.GetRequiredService<T>())
		{
		}
	}


}
