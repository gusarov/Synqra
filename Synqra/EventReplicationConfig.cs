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

	/// <summary>Hello_SubscribeToSnapshot — as <see cref="SubscribeTo"/>, from a fully cold client (no watermark, empty local
	/// log) that asks for a stream snapshot first. The master answers with <see cref="SnapshotBegin"/> … <see cref="SnapshotEnd"/>
	/// or <see cref="SnapshotDeclined"/>, then the backlog (plans/snapshot.md).</summary>
	SubscribeToSnapshot = 4,
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
	/// Whether a fully cold client (no server watermark, an empty local log) asks for its stream's state instead of its history
	/// (<see cref="ReplicationHelloKind.SubscribeToSnapshot"/>, plans/snapshot.md). It takes the stream this connection serves
	/// (<see cref="StreamId"/>, else <see cref="InitialSubscribeStreamId"/>) and applies to the <see cref="ReplicationHelloKind.UserDefaultMainStream"/>
	/// and <see cref="ReplicationHelloKind.SubscribeTo"/> kinds, and only on a client whose projection folds a snapshot (it registers
	/// <see cref="StreamSnapshotFolding"/>, as the in-memory store does). A master that serves none declines and the client replays.
	/// </summary>
	public virtual bool RequestSnapshot { get; set; } = true;

	/// <summary>
	/// How long a connection may stay silent before its catch-up is in: with no frame and no <see cref="CaughtUp"/> for this long,
	/// the client takes the connection for dead, closes it and connects again from its watermark. Zero or less turns it off.
	/// </summary>
	public virtual TimeSpan CatchUpTimeout { get; set; } = TimeSpan.FromSeconds(20);

	/// <summary>
	/// How long a caught-up connection may hear nothing before the client asks the master whether it is still there (a
	/// <see cref="CatchUpRequest"/>, which any frame answers). A master gone without a close (a restart behind a proxy, a sleep, a
	/// network change) otherwise left the connection online and caught up, and the page stale until a reload. Zero or less turns it off.
	/// </summary>
	public virtual TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

	/// <summary>How long that question may go unanswered before the connection is taken for dead and opened again from the watermark.</summary>
	public virtual TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>
	/// The probe's timeout while writes await their ack: the master answers frames in order, and a write it holds behind its global
	/// gate (a peer's snapshot or seeding) holds the answer too, so the shorter timeout reconnected a live connection.
	/// </summary>
	public virtual TimeSpan HeartbeatTimeoutWhileWriting { get; set; } = TimeSpan.FromSeconds(45);

	/// <summary>
	/// This client's build when its host knows it, compared with the master's (<see cref="ServerVersion"/>); null takes the build of
	/// the first master this run talks to as its own.
	/// </summary>
	public virtual string? ClientBuild { get; set; }

	/// <summary>
	/// How long a connect attempt may take before it is given up and tried again: a proxy holds the upgrade of a master still
	/// starting for up to minutes. Zero or less waits as long as the transport does.
	/// </summary>
	public virtual TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

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

