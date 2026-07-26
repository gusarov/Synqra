namespace Synqra.Replication.AspNetCore;

/// <summary>
/// The per-connection stream-routing rule for <see cref="SynqraReplicationEndpointExtensions"/>,
/// factored out so the isolation decision is asserted directly rather than only through a live
/// WebSocket. One rule covers both directions the endpoint filters on:
/// <list type="bullet">
/// <item>backlog replay — an event is sent to a connection only if it belongs to a stream that connection may read;</item>
/// <item>live broadcast — an event is fanned out to a peer socket only if that peer may read the event's stream.</item>
/// </list>
/// <para>
/// <paramref name="connectionStream"/> is the connection's own writable stream — the ambient
/// <see cref="SynqraStreamContext"/> the host established (for Quotaly, the authenticated user's own
/// stream). <c>null</c> means the host set no scope at all — a single-tenant deployment — in which
/// case every event is admitted, exactly the pre-isolation behavior.
/// </para>
/// <para>
/// <paramref name="activeStreams"/> is the set of streams the connection is currently <em>subscribed</em>
/// to read — its own writable stream and/or host-granted shared streams (for Quotaly, e.g. the shared
/// metering/main-menu content streams), as chosen by the HELLO subscription mode and any live
/// Subscribe/Unsubscribe control frames. It is host-authorized and never taken raw from the wire, so
/// admitting these onto a scoped connection is safe. Crucially the own writable stream is <em>not</em>
/// implicitly readable: a connection that opened in EMPTY mode (empty active set) receives nothing —
/// not even its own stream — until it subscribes. An unscoped connection still sees everything.
/// </para>
/// </summary>
internal static class ReplicationStreamScope
{
	/// <summary>
	/// True if an event on stream <paramref name="candidateStream"/> may be delivered to a connection
	/// whose own writable stream is <paramref name="connectionStream"/> and whose currently-subscribed
	/// read-set is <paramref name="activeStreams"/>. A scoped connection admits exactly the streams in
	/// its active set (a peer/event that carries no stream, <c>null</c>, is never admitted onto a scoped
	/// connection, and neither is the own stream unless it is in the active set); an unscoped connection
	/// (<paramref name="connectionStream"/> is <c>null</c>) sees everything.
	/// </summary>
	public static bool Admits(System.Guid? candidateStream, System.Guid? connectionStream, System.Collections.Generic.IReadOnlySet<System.Guid>? activeStreams = null)
		=> connectionStream is not System.Guid
			|| (candidateStream is System.Guid candidate && activeStreams is not null && activeStreams.Contains(candidate));
}
