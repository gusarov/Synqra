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
/// <paramref name="readableStreams"/> is an optional host-supplied set of <em>additional</em> streams
/// the connection may <em>read</em> (never write) — genuinely shared/public streams the host chose to
/// fan out to every connection (for Quotaly, e.g. the shared metering/main-menu content streams). It is
/// host-controlled and never taken from the wire, so admitting them onto a scoped connection is safe.
/// A scoped connection therefore sees its own stream plus these; an unscoped connection still sees all.
/// </para>
/// </summary>
internal static class ReplicationStreamScope
{
	/// <summary>
	/// True if an event on stream <paramref name="candidateStream"/> may be delivered to a connection
	/// whose own writable stream is <paramref name="connectionStream"/> and which may additionally read
	/// <paramref name="readableStreams"/>. A scoped connection sees its own stream and any host-supplied
	/// readable stream (a peer that carries no stream, <c>null</c>, is never admitted onto a scoped
	/// connection); an unscoped connection sees everything.
	/// </summary>
	public static bool Admits(System.Guid? candidateStream, System.Guid? connectionStream, System.Collections.Generic.IReadOnlySet<System.Guid>? readableStreams = null)
		=> connectionStream is not System.Guid stream
			|| candidateStream == stream
			|| (candidateStream is System.Guid candidate && readableStreams is not null && readableStreams.Contains(candidate));
}
