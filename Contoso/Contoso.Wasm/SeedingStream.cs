using System.Web;
using Synqra;

namespace Contoso.Wasm;

/// <summary>
/// The stream a page load replicates, named by its URL (<c>?stream=</c>) and fixed for the whole load. Its events live in a log of
/// its own, as Quotaly pins its browser log to the signed-in stream: a browser log serves one stream (an SBX record does not keep
/// its stream). Without a stream the page replicates nothing.
/// </summary>
public sealed class SeedingStream
{
	readonly Uri _page;

	public SeedingStream(string pageUrl)
	{
		_page = new Uri(pageUrl);
		StreamId = Guid.TryParse(HttpUtility.ParseQueryString(_page.Query)["stream"], out var stream) ? stream : default;
	}

	public Guid StreamId { get; }

	public bool IsSelected => StreamId != default;

	/// <summary>The IndexedDB store of this load's log: one per stream.</summary>
	public string StoreName => IsSelected ? $"Events-{StreamId:N}" : "Event";

	/// <summary>Where this tab records a stream it created (sessionStorage) until that stream's own page load, in this tab, seeds it.</summary>
	public string CreationRecordKey => ComposeCreationRecordKey(StreamId);

	public string ComposeCreationRecordKey(Guid stream) => $"synqra-contoso-created-{stream:N}";

	/// <summary>The replication client's config: the page's own origin, scoped by the server to this stream.</summary>
	public EventReplicationConfig CreateReplicationConfig()
	{
		var scheme = _page.Scheme == "https" ? "wss" : "ws";
		var port = _page.IsDefaultPort ? "" : $":{_page.Port}";
		return new EventReplicationConfig
		{
			Endpoint = $"{scheme}://{_page.Host}{port}/api/synqra/ws?stream={StreamId:D}",
			HelloKind = ReplicationHelloKind.UserDefaultMainStream,
		};
	}
}
