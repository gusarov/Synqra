using System.Collections.Concurrent;

namespace Synqra.Replication.AspNetCore;

/// <summary>
/// The event ids seen recently, for deduplicating what the endpoint broadcasts. Bounded: once full,
/// the oldest id is forgotten for each new one. An event older than the window could be broadcast
/// twice, which a client tolerates (it dedups by event id itself); an unbounded set would not be
/// tolerated by a host that runs for months.
/// </summary>
internal sealed class RecentEventIds(int capacity)
{
	readonly ConcurrentDictionary<Guid, byte> _seen = new();
	readonly ConcurrentQueue<Guid> _order = new();

	/// <summary>True when the id was not seen within the window.</summary>
	public bool TryAdd(Guid eventId)
	{
		var added = _seen.TryAdd(eventId, 0);
		if (added)
		{
			_order.Enqueue(eventId);
			while (_order.Count > capacity && _order.TryDequeue(out var oldest))
			{
				_seen.TryRemove(oldest, out _);
			}
		}
		return added;
	}
}
