namespace Synqra.Replication.AspNetCore;

/// <summary>
/// The per-connection stream-routing rule for <see cref="SynqraReplicationEndpointExtensions"/>,
/// factored out so the isolation decision is asserted directly rather than only through a live
/// WebSocket. One rule covers both directions the endpoint filters on:
/// <list type="bullet">
/// <item>backlog replay — an event is sent to a connection only if it belongs to that connection's stream;</item>
/// <item>live broadcast — an event is fanned out to a peer socket only if that peer is on the event's stream.</item>
/// </list>
/// <para>
/// <paramref name="connectionStream"/> is the stream the receiving connection is authorized for — the
/// ambient <see cref="SynqraStreamContext"/> the host established (for Quotaly, the authenticated
/// user's own stream). <c>null</c> means the host set no scope at all — a single-tenant deployment —
/// in which case every event is admitted, exactly the pre-isolation behavior.
/// </para>
/// </summary>
internal static class ReplicationStreamScope
{
	/// <summary>
	/// True if an event on stream <paramref name="candidateStream"/> may be delivered to a connection
	/// scoped to <paramref name="connectionStream"/>. A scoped connection only ever sees its own
	/// stream (a peer that carries no stream, <c>null</c>, is never admitted onto a scoped connection);
	/// an unscoped connection sees everything.
	/// </summary>
	public static bool Admits(System.Guid? candidateStream, System.Guid? connectionStream)
		=> connectionStream is not System.Guid stream || candidateStream == stream;
}
