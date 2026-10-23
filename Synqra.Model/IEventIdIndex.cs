namespace Synqra;

/// <summary>
/// The EventIds a log holds, in no particular order. A log that keeps them in an index (the browser's IndexedDB tags) answers
/// without reading a single event; any other reads its events. The replication client seeds its dedup from it at start.
/// </summary>
public interface IEventIdIndex
{
	IAsyncEnumerable<Guid> EnumerateEventIdsAsync(CancellationToken cancellationToken = default);
}
