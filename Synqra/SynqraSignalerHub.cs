using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Synqra;

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
