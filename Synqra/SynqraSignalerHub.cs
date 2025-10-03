using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Synqra;

// [UniAuthorize]
/// <summary>
/// Server-side Service that accept WS connections from clients
/// </summary>
public class SynqraSignalerHub : Hub // Hub<IEventHubClient>
{
	private readonly ILogger _logger;
	private readonly JsonSerializerOptions _jsonSerializerOptions;

	public SynqraSignalerHub(ILogger<SynqraSignalerHub> logger, JsonSerializerOptions jsonSerializerOptions)
	{
		_logger = logger;
		_jsonSerializerOptions = jsonSerializerOptions;

		if (_jsonSerializerOptions.Converters.Count == 0)
		{
			throw new ArgumentException("The jsonSerializerOptions parameter is invalid because it has no converters.", nameof(jsonSerializerOptions));
		}
	}

	public override Task OnConnectedAsync()
	{
		var user = Context?.User;
		Trace.WriteLine($"Connected SignalR: UserName= UserId= ConnectionId={Context.ConnectionId}");
		_logger.LogWarning($"Connected SignalR: UserName= UserId= ConnectionId={Context.ConnectionId}");
		return base.OnConnectedAsync();
	}

	public override Task OnDisconnectedAsync(Exception exception)
	{
		return base.OnDisconnectedAsync(exception);
	}

	[HubMethodName("NewEvent1")]
	public async Task NewEvent1(Event ev) => await Clients.All.SendAsync("NewEvent1", ev);

	[HubMethodName("Hello1")]
	public async Task Hello1(Guid nodeId, long lastKnownEventId)
	{
		Console.WriteLine();
	}
}
