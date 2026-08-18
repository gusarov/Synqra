using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

/// <summary>
/// What a replication client asks the master to start pushing right after HELLO — the "ws-method".
/// This one <em>is</em> a raw wire byte rather than an SBX message, and legitimately so: HELLO carries
/// the magic that negotiates the serializer, so it is parsed before any serializer is agreed on.
/// Everything after HELLO is a proper <see cref="TransportOperation"/>. Whatever the choice, the master
/// replies with an authoritative <see cref="SubscriptionState"/> ack so the client can detect an
/// unexpected default.
/// </summary>
public enum ReplicationHelloKind : byte
{
	/// <summary>Not a kind — the reserved zero, so an absent/zeroed kind byte is rejected loudly instead
	/// of silently selecting whichever mode happened to sit at 0. Note the config default is
	/// <see cref="UserDefaultMainStream"/>, so a zero here would otherwise disagree with it.</summary>
	Unknown = 0,

	/// <summary>Hello_NoAutoSubscription — start subscribed to nothing (not even the own stream) until
	/// the client issues its own Subscribe frames. For UIs that drive their own per-view subscriptions.</summary>
	NoAutoSubscription = 1,

	/// <summary>Hello_Subscribed_UserDefaultMainStream — start subscribed to just the user's own default
	/// main stream. The simple "give me my data" default.</summary>
	UserDefaultMainStream = 2,

	/// <summary>Hello_SubscribeTo — start subscribed to one specific stream named in the HELLO (see
	/// <see cref="EventReplicationConfig.InitialSubscribeStreamId"/>), if the host ceiling authorizes it.</summary>
	SubscribeTo = 3,
}

public class EventReplicationConfig
{
	public virtual ushort Port { get; set; }

	/// <summary>
	/// The HELLO "ws-method" announced in the handshake. <see cref="ReplicationHelloKind.UserDefaultMainStream"/>
	/// (start subscribed to the own main stream) unless a self-subscribing client chooses
	/// <see cref="ReplicationHelloKind.NoAutoSubscription"/> or <see cref="ReplicationHelloKind.SubscribeTo"/>.
	/// </summary>
	public virtual ReplicationHelloKind HelloKind { get; set; } = ReplicationHelloKind.UserDefaultMainStream;

	/// <summary>
	/// The stream to subscribe to at HELLO when <see cref="HelloKind"/> is
	/// <see cref="ReplicationHelloKind.SubscribeTo"/>. Ignored for the other kinds.
	/// </summary>
	public virtual Guid? InitialSubscribeStreamId { get; set; }

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

