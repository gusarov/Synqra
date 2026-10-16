using Synqra.AppendStorage;

namespace Synqra;

/// <summary>
/// The server's event log: stamps every appended event's <see cref="Event.SequenceId"/> through the
/// <see cref="EventSequencer"/> (docs/model.md §9), then reports the commit through <see cref="EventsCommitted"/> under
/// the stream's lock, so a subscriber reading <see cref="ReadWatermark"/> gets the watermark of that very commit. Wraps
/// any <see cref="IAppendStorage{T, TKey}"/>; one that is an <see cref="IEventSequenceTail"/> seeds a stream's clock with
/// one query, any other with one scan.
/// </summary>
public sealed class SequencedEventLog : IAppendStorage<Event, Guid>, ICommittedEventSource, IEventWatermarks
{
	readonly IAppendStorage<Event, Guid> _inner;
	readonly EventSequencer _sequencer;

	public SequencedEventLog(IAppendStorage<Event, Guid> inner, EventSequencer sequencer)
	{
		_inner = inner;
		_sequencer = sequencer;
	}

	public event Action<IEnumerable<Event>>? EventsCommitted;

	public Guid ReadWatermark(Guid streamId) => _sequencer.ReadWatermark(streamId);

	public async Task AppendAsync(Event item, CancellationToken cancellationToken = default)
	{
		await SeedAsync(item.StreamId, cancellationToken);
		var stamp = Stamp(item);
		try
		{
			await _inner.AppendAsync(item, cancellationToken);
		}
		catch
		{
			_sequencer.Resolve(item.StreamId, [stamp], committed: false);
			throw;
		}
		Event[] committed = [item];
		_sequencer.Resolve(item.StreamId, [stamp], committed: true, () => EventsCommitted?.Invoke(committed));
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
			if (batch.Any(ev => ev.StreamId != streamId))
			{
				throw new InvalidOperationException("SequencedEventLog: a batch must belong to one stream.");
			}
			await SeedAsync(streamId, cancellationToken);
			var stamps = new Guid[batch.Count];
			for (var i = 0; i < batch.Count; i++)
			{
				stamps[i] = Stamp(batch[i]);
			}
			try
			{
				await _inner.AppendBatchAsync(batch, cancellationToken);
			}
			catch
			{
				_sequencer.Resolve(streamId, stamps, committed: false);
				throw;
			}
			_sequencer.Resolve(streamId, stamps, committed: true, () => EventsCommitted?.Invoke(batch));
		}
	}

	Guid Stamp(Event item)
	{
		if (item.SequenceId == default)
		{
			item.SequenceId = _sequencer.Stamp(item.StreamId, item.EventId); // the client's proposal is its own id
		}
		else
		{
			_sequencer.Track(item.StreamId, item.SequenceId); // a retried append: the inner log skips what it already holds
		}
		return item.SequenceId;
	}

	async Task SeedAsync(Guid streamId, CancellationToken cancellationToken)
	{
		if (!_sequencer.IsSeeded(streamId))
		{
			Guid tail;
			if (_inner is IEventSequenceTail tails)
			{
				tail = await tails.LastSequenceAsync(streamId, cancellationToken);
			}
			else
			{
				tail = default;
				await foreach (var ev in _inner.GetAllAsync(default, cancellationToken))
				{
					if (ev.StreamId == streamId && ev.SequenceId.CompareBigEndian(tail) > 0)
					{
						tail = ev.SequenceId;
					}
				}
			}
			_sequencer.Seed(streamId, tail);
		}
	}

	public Task<Event> GetAsync(Guid key, CancellationToken cancellationToken = default) => _inner.GetAsync(key, cancellationToken);

	public IAsyncEnumerable<Event> GetAllAsync(Guid from = default, CancellationToken cancellationToken = default) => _inner.GetAllAsync(from, cancellationToken);

	public Task FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);

	public void Dispose() => _inner.Dispose();

	public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
