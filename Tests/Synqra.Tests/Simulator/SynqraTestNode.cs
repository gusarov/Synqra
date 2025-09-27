using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synqra.Storage;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.Helpers;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra.Tests.Simulator;

internal class SynqraTestNode
{
	public WebApplication Host { get; private set; }

	public ISynqraStoreContext StoreContext { get => field ??= Host.Services.GetRequiredService<ISynqraStoreContext>(); }

	public ushort Port { get; set; }

	public SynqraTestNode(Action<IHostApplicationBuilder>? configureHost = null, bool masterHost = false)
	{
		#region Folder

		var utils = new TestUtils();
		var synqraTestsCurrentPath = utils.CreateTestFolder();

		#endregion

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
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
		builder.AddJsonLinesStorage();
		builder.Services.AddSingleton<JsonSerializerContext>(TestJsonSerializerContext.Default);
		builder.Services.AddSingleton(TestJsonSerializerContext.Default.Options);

		builder.Services.AddSignalR();

		if (masterHost)
		{
		}
		else
		{
			builder.Services.AddHostedService<EventReplicationService>();
			builder.Services.AddSingleton<EventReplicationState>();
			// builder.Services.Configure<EventReplicationConfig>(builder.Configuration.GetSection(nameof(EventReplicationConfig)));
			builder.Services.Configure<EventReplicationConfig>(x =>
			{
				x.Port = port;
			});
		}

		configureHost?.Invoke(builder);
		var app = builder.Build();
		Host = app;

		if (masterHost)
		{
			app.MapHub<SynqraSignalerHub>("/api/synqra");
		}

		_ = Host.StartAsync();
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

// [UniAuthorize]
/// <summary>
/// Server-side Service that accept WS connections from clients
/// </summary>
public class SynqraSignalerHub : Hub<IEventHubClient>
{
	private readonly ILogger _logger;

	public SynqraSignalerHub(ILogger<SynqraSignalerHub> logger)
	{
		_logger = logger;
	}

	public override Task OnConnectedAsync()
	{
		var user = Context?.User;
		Trace.WriteLine($"Connected SignalR: UserName= UserId= ConnectionId={Context.ConnectionId}");
		_logger.LogWarning($"Connected SignalR: UserName= UserId= ConnectionId={Context.ConnectionId}");
		return base.OnConnectedAsync();
	}
}

/// <summary>
/// This is SignalR client contract
/// </summary>
public interface IEventHubClient
{
	// DO NOT RENAME! Clients receive this method name
	public Task NewEvent(Event eventObject);
}

public class EventReplicationConfig
{
	public ushort Port { get; set; }
}

// Persistent state with metadata about replication, e.g. known version vectors, node ids
public class EventReplicationState
{
	string _fileName;
	JsonSerializerContext _jsonSerializerContext;

	public EventReplicationState(IHostEnvironment hostEnvironment, JsonSerializerContext jsonSerializerContext)
	{
		_jsonSerializerContext = jsonSerializerContext ?? throw new ArgumentNullException(nameof(jsonSerializerContext));
		_fileName = Path.Combine(hostEnvironment.ContentRootPath, "EventReplicationState.json");

		if (File.Exists(_fileName))
		{
			this.RSetSTJ(File.ReadAllText(_fileName), jsonSerializerContext);
		}
		else
		{
			MyNodeId = Guid.NewGuid();
			Save();
		}
	}

	public void Save()
	{
		File.WriteAllText(_fileName, JsonSerializer.Serialize(this, _jsonSerializerContext.Options));
	}

	public Guid MyNodeId { get; set; }
}

/// <summary>
/// Client-side service that connect to WS Master
/// </summary>
public class EventReplicationService : IHostedService
{
	private readonly IHubContext<SynqraSignalerHub, IEventHubClient> _hubContext;
	private readonly IStorage _storage;
	private readonly EventReplicationConfig _config;

	public EventReplicationService(IHubContext<SynqraSignalerHub, IEventHubClient> hubContext, IOptions<EventReplicationConfig> options, IStorage storage)
	{
		_hubContext = hubContext;
		_storage = storage;
		_config = options.Value;
	}

	public Task BroadcastEvent(Event eventObject)
	{
		return _hubContext.Clients.All.NewEvent(eventObject);
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await Task.Yield();
		_ = StartWorker();
	}

	async Task StartWorker()
	{
		var connection = new HubConnectionBuilder()
			.WithUrl($"http://localhost:{_config.Port}/api/synqra")
			.WithAutomaticReconnect()
			.Build();
		connection.On<Event>("NewEvent", async (eventObject) =>
		{
			throw new NotImplementedException();
			await _storage.AppendAsync(new[] { eventObject });
		});
		await connection.StartAsync();
		connection.InvokeAsync("Hello1", Guid.NewGuid(), 0L); // client send hello message to server. Parameters are: client id (guid me), last known event id (long)
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}
}