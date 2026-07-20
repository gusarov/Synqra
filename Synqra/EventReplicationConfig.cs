using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.WebSockets;

namespace Synqra;

public class EventReplicationConfig
{
	public virtual ushort Port { get; set; }

	/// <summary>
	/// Full WebSocket endpoint URI for the replication master. When set, overrides the default
	/// <c>ws://localhost:{Port}/api/synqra/ws</c> construction so a client can connect to
	/// the correct remote host in deployed environments.
	/// </summary>
	public virtual string? Endpoint { get; set; }

	/// <summary>
	/// Configures a new WebSocket before each connection attempt. Return false when the client
	/// is not currently eligible to connect, for example while it has no authenticated session.
	/// </summary>
	public Func<ClientWebSocket, CancellationToken, Task<bool>>? ConfigureWebSocketAsync { get; set; }

	/// <summary>
	/// Resolves the local stream whose confirmed records and pending commands should participate in
	/// this connection. The server still derives the authoritative stream from authentication.
	/// </summary>
	public Func<CancellationToken, Task<Guid?>>? ResolveStreamIdAsync { get; set; }

	internal Uri ResolveEndpointUri() =>
		Endpoint is not null ? new Uri(Endpoint) : new Uri($"ws://localhost:{Port}/api/synqra/ws");
}

public class DelegatedEventReplicationConfig : EventReplicationConfig
{
	private readonly Func<ushort> _func;

	public DelegatedEventReplicationConfig(Func<ushort> func)
	{
		_func = func;
	}

	public override ushort Port => _func();
}

