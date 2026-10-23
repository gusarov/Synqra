using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;

namespace Synqra;

/// <summary>
/// An event log over a storage keyed by <c>(StreamId, SequenceId)</c>: every append stamps the event's
/// <see cref="Event.SequenceId"/> as its position in this log, under the stream's <see cref="EventSequencer"/>, then
/// reports the commit through <see cref="EventsCommitted"/> under the stream's lock, so a subscriber reading
/// <see cref="ReadWatermark"/> gets the watermark of that very commit. The two logs differ only in how they stamp:
/// <see cref="ServerEventLog"/> keeps a client's proposal (commit order), <see cref="ClientEventLog"/> takes the next
/// local stamp (arrival order). A stamp from another log is never kept: one log, one clock.
/// </summary>
public abstract class EventLog : IEventLog, ICommittedEventSource, IEventWatermarks, IEventLookup, IEventIdIndex
{
	readonly IAppendStorage<Event, (Guid Stream, Guid Sequence)> _inner;
	readonly EventSequencer _sequencer;

	protected EventLog(IAppendStorage<Event, (Guid Stream, Guid Sequence)> inner, EventSequencer sequencer)
	{
		_inner = inner;
		_sequencer = sequencer;
	}

	public event Action<IEnumerable<Event>>? EventsCommitted;

	public Guid ReadWatermark(Guid streamId) => _sequencer.ReadWatermark(streamId);

	/// <summary>Seeds the stream from the log's tail first: a process that has not written it yet still hands out a real position.</summary>
	public async ValueTask<Guid> ReadWatermarkAsync(Guid streamId, CancellationToken cancellationToken = default)
	{
		await SeedAsync(streamId, cancellationToken);
		return ReadWatermark(streamId);
	}

	/// <summary>The storage's own index when it keeps one; otherwise every event is read.</summary>
	public async IAsyncEnumerable<Guid> EnumerateEventIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		if (_inner is IEventIdIndex index)
		{
			await foreach (var id in index.EnumerateEventIdsAsync(cancellationToken))
			{
				yield return id;
			}
		}
		else
		{
			await foreach (var ev in _inner.GetAllAsync(default, cancellationToken))
			{
				yield return ev.EventId;
			}
		}
	}

	/// <summary>By the storage's identity index when it has one; a scan of the stream otherwise (in-memory and file logs are small).</summary>
	public async Task<bool> ContainsAsync(Guid streamId, Guid eventId, CancellationToken cancellationToken = default)
	{
		bool contains;
		if (_inner is IEventLookup lookup)
		{
			contains = await lookup.ContainsAsync(streamId, eventId, cancellationToken);
		}
		else
		{
			contains = false;
			await foreach (var ev in ReadStreamAsync(streamId, default, cancellationToken))
			{
				if (ev.EventId == eventId)
				{
					contains = true;
				}
			}
		}
		return contains;
	}

	/// <summary>Stamps <paramref name="item"/>'s sequence for this log and returns it.</summary>
	protected abstract Guid Stamp(Event item, EventSequencer sequencer);

	public async Task AppendAsync(Event item, CancellationToken cancellationToken = default)
	{
		RequireStream(item);
		await SeedAsync(item.StreamId, cancellationToken);
		Event[] batch = [item];
		await AppendStampedAsync(item.StreamId, batch, [Stamp(item, _sequencer)], () => _inner.AppendAsync(item, cancellationToken));
	}

	public async Task AppendBatchAsync(IEnumerable<Event> items, CancellationToken cancellationToken = default)
	{
		var batch = items as IReadOnlyList<Event> ?? items.ToArray(); // stamped before and resolved after the insert: two passes
		if (batch.Count == 1)
		{
			await AppendAsync(batch[0], cancellationToken);
		}
		else if (batch.Count > 1)
		{
			var streamId = batch[0].StreamId; // a batch is one command's events: one stream
			foreach (var item in batch)
			{
				RequireStream(item);
				if (item.StreamId != streamId)
				{
					throw new InvalidOperationException("EventLog: a batch must belong to one stream.");
				}
			}
			await SeedAsync(streamId, cancellationToken);
			var stamps = new Guid[batch.Count];
			for (var i = 0; i < batch.Count; i++)
			{
				stamps[i] = Stamp(batch[i], _sequencer);
			}
			await AppendStampedAsync(streamId, batch, stamps, () => _inner.AppendBatchAsync(batch, cancellationToken));
		}
	}

	/// <summary>Runs the insert; its stamps are resolved whatever happens, committed only when it returned.</summary>
	async Task AppendStampedAsync(Guid streamId, IReadOnlyList<Event> batch, Guid[] stamps, Func<Task> insert)
	{
		var committed = false;
		try
		{
			await insert();
			committed = true;
		}
		finally
		{
			_sequencer.Resolve(streamId, stamps, committed, () => EventsCommitted?.Invoke(batch));
		}
	}

	/// <summary>The key is (stream, sequence): an event without its stream would be filed outside every stream.</summary>
	void RequireStream(Event item)
	{
		if (item.StreamId == default)
		{
			throw new InvalidOperationException($"Event {item.EventId} has no StreamId: an event log files every event under its stream.");
		}
	}

	async Task SeedAsync(Guid streamId, CancellationToken cancellationToken)
	{
		if (!_sequencer.IsSeeded(streamId))
		{
			var tail = default(Guid);
			if (_inner is IEventSequenceTail tails)
			{
				tail = await tails.LastSequenceAsync(streamId, cancellationToken);
			}
			else
			{
				await foreach (var ev in ReadStreamAsync(streamId, default, cancellationToken))
				{
					if (ev.SequenceId.CompareBigEndian(tail) > 0)
					{
						tail = ev.SequenceId;
					}
				}
			}
			_sequencer.Seed(streamId, tail);
		}
	}

	/// <summary>One stream after a cursor: bounded by the storage when it can (<see cref="IEventStreamReader"/>), else the key range stopped at the next stream.</summary>
	public IAsyncEnumerable<Event> ReadStreamAsync(Guid streamId, Guid after = default, CancellationToken cancellationToken = default)
		=> _inner is IEventStreamReader reader ? reader.ReadStreamAsync(streamId, after, cancellationToken) : ReadKeyRangeAsync(streamId, after, cancellationToken);

	// Safe without a bound: the in-memory storage deserializes nothing, and SBX/JSON-lines storages hold one stream each.
	async IAsyncEnumerable<Event> ReadKeyRangeAsync(Guid streamId, Guid after, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var inStream = true;
		await using var events = _inner.GetAllAsync((streamId, after), cancellationToken).GetAsyncEnumerator(cancellationToken);
		while (inStream && await events.MoveNextAsync())
		{
			var ev = events.Current;
			inStream = ev.StreamId == default || ev.StreamId == streamId; // a store that drops the stream on read (SBX, JSON lines) holds one stream
			if (inStream)
			{
				yield return ev;
			}
		}
	}

	public Task<Event> GetAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default) => _inner.GetAsync(key, cancellationToken);

	public IAsyncEnumerable<Event> GetAllAsync((Guid Stream, Guid Sequence) from = default, CancellationToken cancellationToken = default) => _inner.GetAllAsync(from, cancellationToken);

	public Task FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);

	public void Dispose() => _inner.Dispose();

	public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// The server's log: commit order. A client's own EventId is its proposal, kept when it follows the stream and is not
/// too far ahead of the clock, fast-forwarded otherwise. An event that already carries a stamp is a retry of this log's
/// own append (a projection transaction retried): tracked, not stamped again. The replication endpoint clears an inbound
/// event's stamp first, so a client's local stamp never reaches here as a retry.
/// </summary>
public sealed class ServerEventLog : EventLog
{
	public ServerEventLog([FromKeyedServices] IAppendStorage<Event, (Guid Stream, Guid Sequence)> inner, [FromKeyedServices] EventSequencer sequencer)
		: base(inner, sequencer)
	{
	}

	protected override Guid Stamp(Event item, EventSequencer sequencer)
	{
		if (item.SequenceId == default)
		{
			item.SequenceId = sequencer.Stamp(item.StreamId, item.EventId);
		}
		else
		{
			sequencer.Track(item.StreamId, item.SequenceId);
		}
		return item.SequenceId;
	}
}

/// <summary>
/// A client's local log: arrival order. Every event, its own or one received from the server, takes the next local
/// stamp, so a catch-up from a cursor sees everything that arrived after it, whoever wrote it and whatever its id.
/// </summary>
public sealed class ClientEventLog : EventLog
{
	public ClientEventLog([FromKeyedServices] IAppendStorage<Event, (Guid Stream, Guid Sequence)> inner, [FromKeyedServices] EventSequencer sequencer)
		: base(inner, sequencer)
	{
	}

	protected override Guid Stamp(Event item, EventSequencer sequencer)
	{
		item.SequenceId = sequencer.Stamp(item.StreamId, default);
		return item.SequenceId;
	}
}
