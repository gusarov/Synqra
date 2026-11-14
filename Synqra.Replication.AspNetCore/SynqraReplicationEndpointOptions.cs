namespace Synqra.Replication.AspNetCore;

/// <summary>
/// Tunes the replication endpoint (<see cref="SynqraReplicationEndpointExtensions.MapSynqraReplicationEndpoint"/>):
/// <c>services.Configure&lt;SynqraReplicationEndpointOptions&gt;(...)</c>.
/// </summary>
public sealed class SynqraReplicationEndpointOptions
{
	/// <summary>
	/// How long a connecting client's backlog read may yield nothing before the endpoint aborts the connection with a logged
	/// error, so the client reconnects instead of waiting on a silent socket (a hung Mongo getMore held one open with no
	/// <see cref="CaughtUp"/> ever sent). The time a frame takes to reach a slow client does not count. Zero or less turns it off.
	/// </summary>
	public TimeSpan BacklogReadTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
