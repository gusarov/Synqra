using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Synqra;

/// <summary>
/// A server log's commit order, one stream at a time (docs/model.md §9). The client proposes its own
/// <see cref="Event.EventId"/> as the sequence; a proposal that follows the stream's last stamp and is not ahead of
/// the server clock by more than <see cref="MaxAhead"/> is kept, any other is fast-forwarded past the last stamp by
/// the stream's own <see cref="GuidExtensions.Generator"/>. Stamps are issued before the insert resolves, so the
/// watermark (what a client may resume from) trails the lowest stamp still in flight.
/// </summary>
public sealed class EventSequencer
{
	readonly ConcurrentDictionary<Guid, StreamSequence> _streams = new();
	readonly GuidOrder _order = new();

	public EventSequencer(TimeSpan? maxAhead = null)
	{
		MaxAhead = maxAhead ?? TimeSpan.FromMinutes(5);
	}

	/// <summary>How far ahead of the server clock a proposal may run and still be kept (the Kerberos five minutes).</summary>
	public TimeSpan MaxAhead { get; }

	/// <summary>Whether the stream's clock has been seeded from the log's tail; a cold stream must be seeded before its first stamp.</summary>
	public bool IsSeeded(Guid streamId) => Of(streamId).Seeded;

	/// <summary>Seeds the stream's clock from its highest committed sequence (default = empty stream). Idempotent.</summary>
	public void Seed(Guid streamId, Guid tail)
	{
		var stream = Of(streamId);
		lock (stream)
		{
			if (!stream.Seeded)
			{
				if (tail != default && tail.GetVersion() == 7)
				{
					stream.Generator.CreateVersion7OrApprove(tail); // the generator's clock now follows the tail
					stream.Watermark = tail;
				}
				else if (tail != default)
				{
					EmergencyLog.Default.LogWarning($"EventSequencer: stream {streamId} tail {tail} is not a v7 id; new sequences start from the server clock and may sort before it."); // legacy ids: tolerable until the log is migrated
				}
				stream.Seeded = true;
			}
		}
	}

	/// <summary>Issues the sequence for an event about to be inserted; <see cref="Resolve"/> must follow once the insert settled.</summary>
	public Guid Stamp(Guid streamId, Guid proposal)
	{
		var stream = Of(streamId);
		Guid stamped;
		lock (stream)
		{
			if (!stream.Seeded)
			{
				throw new InvalidOperationException($"EventSequencer: stream {streamId} is not seeded; seed it from the log's tail before stamping.");
			}
			if (proposal != default && proposal.GetVersion() == 7 && proposal.GetTimestamp() <= DateTime.UtcNow + MaxAhead)
			{
				stamped = stream.Generator.CreateVersion7OrApprove(proposal); // kept as is when it follows, fast-forwarded otherwise
			}
			else
			{
				stamped = stream.Generator.CreateVersion7(); // not a clock id, or too far ahead: the server clock decides
			}
			stream.InFlight.Add(stamped);
		}
		return stamped;
	}

	/// <summary>Accounts for an event that already carries its sequence (a retried append): in flight until resolved, never re-stamped.</summary>
	public void Track(Guid streamId, Guid stamp)
	{
		var stream = Of(streamId);
		lock (stream)
		{
			stream.InFlight.Add(stamp);
		}
	}

	/// <summary>
	/// Settles stamps after their insert: committed ones advance the watermark as far as no lower stamp is still in flight;
	/// <paramref name="onCommitted"/> runs inside the stream's lock, so what it reads through <see cref="ReadWatermark"/>
	/// is exactly the watermark these events were committed under.
	/// </summary>
	public void Resolve(Guid streamId, IEnumerable<Guid> stamps, bool committed, Action? onCommitted = null)
	{
		var stream = Of(streamId);
		lock (stream)
		{
			foreach (var stamp in stamps)
			{
				stream.InFlight.Remove(stamp);
				if (committed)
				{
					stream.Committed.Add(stamp);
				}
			}
			while (stream.Committed.Count > 0 && (stream.InFlight.Count == 0 || _order.Compare(stream.Committed.Min, stream.InFlight.Min) < 0))
			{
				var next = stream.Committed.Min;
				stream.Committed.Remove(next);
				if (_order.Compare(next, stream.Watermark) > 0)
				{
					stream.Watermark = next; // a retried, already-passed sequence never moves it back
				}
			}
			if (committed)
			{
				onCommitted?.Invoke();
			}
		}
	}

	public Guid ReadWatermark(Guid streamId)
	{
		var stream = Of(streamId);
		lock (stream)
		{
			return stream.Watermark;
		}
	}

	StreamSequence Of(Guid streamId) => _streams.GetOrAdd(streamId, _ => new StreamSequence(_order));

	sealed class StreamSequence(IComparer<Guid> order)
	{
		public GuidExtensions.Generator Generator { get; } = new();
		public SortedSet<Guid> InFlight { get; } = new(order);
		public SortedSet<Guid> Committed { get; } = new(order);
		public Guid Watermark { get; set; }
		public bool Seeded { get; set; }
	}

	/// <summary>The wire and Mongo order of ids (big-endian bytes), explicit and the same on every target.</summary>
	sealed class GuidOrder : IComparer<Guid>
	{
		public int Compare(Guid x, Guid y) => x.CompareBigEndian(y);
	}
}
