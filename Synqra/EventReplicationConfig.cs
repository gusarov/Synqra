using System.Net.WebSockets;
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

	/// <summary>
	/// When set, this client only <i>sends</i> locally-authored events whose
	/// <see cref="Event.StreamId"/> matches — every other stream's events in the shared multitenant
	/// local store are skipped (their outbound cursor still advances). Lets a process replicate one
	/// specific stream (e.g. a per-user "/tracking" sub-stream) over its own connection without
	/// leaking, or mis-filing, events from another stream that happens to share the same local store.
	/// <c>null</c> (the default) replicates every stream, exactly as before.
	/// <para>
	/// Deliberately mutable: the stream is often per-user and only known after sign-in, so a host can
	/// set it just before the service starts (see the auth-gated starter pattern) rather than at DI
	/// registration time.
	/// </para>
	/// </summary>
	public Guid? StreamId { get; set; }

	/// <summary>
	/// Hook to configure the outgoing <see cref="ClientWebSocket"/> before it connects — the only
	/// seam a host has to attach auth to the replication socket. A native client (MAUI/desktop) sets
	/// <see cref="ClientWebSocketOptions.Cookies"/> to the same <see cref="System.Net.CookieContainer"/>
	/// its authenticated <c>HttpClient</c> uses, so the cookie-auth'd server endpoint accepts the
	/// upgrade. A WASM/browser client leaves this <c>null</c>: the browser attaches its origin cookies
	/// to the WS handshake automatically, and <see cref="ClientWebSocketOptions.Cookies"/> throws
	/// <see cref="PlatformNotSupportedException"/> there anyway.
	/// </summary>
	public Action<ClientWebSocketOptions>? ConfigureSocket { get; set; }

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

