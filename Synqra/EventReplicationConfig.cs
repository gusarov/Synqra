using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

public class EventReplicationConfig
{
	public virtual ushort Port { get; set; }

	/// <summary>
	/// Full WebSocket endpoint URI for the replication master. When set, overrides the default
	/// <c>ws://localhost:{Port}/api/synqra/ws</c> construction so the WASM client can connect to
	/// the correct remote host in deployed environments.
	/// </summary>
	public virtual string? Endpoint { get; set; }

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

