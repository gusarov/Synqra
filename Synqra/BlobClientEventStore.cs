using System.Runtime.CompilerServices;
using Synqra.AppendStorage;
using Synqra.BlobStorage;

namespace Synqra;

public sealed class BlobClientEventStore : IClientEventStore
{
	private readonly IBlobStorage<Guid> _confirmedStorage;
	private readonly IBlobStorage<Guid> _pendingStorage;
	private readonly ReplicationRecordCodec _codec;
	private readonly SemaphoreSlim _gate = new(1, 1);

	public BlobClientEventStore(
		  IBlobStorage<Guid> confirmedStorage
		, IBlobStorage<Guid> pendingStorage
		, ReplicationRecordCodec codec
	)
	{
		_confirmedStorage = confirmedStorage;
		_pendingStorage = pendingStorage;
		_codec = codec;
	}

	public async Task StageAsync(
		  Command command
		, IReadOnlyList<Event> events
		, CommandSubmissionOptions? options = null
		, CancellationToken cancellationToken = default
	)
	{
		if (command.StreamId == Guid.Empty)
		{
			throw new ArgumentException("A pending command requires a stream id.", nameof(command));
		}
		if (events.Any(x => x.CommandId != command.CommandId))
		{
			throw new ArgumentException("Every optimistic event must belong to the staged command.", nameof(events));
		}

		var batch = new PendingCommandBatch
		{
			Command = command,
			Events = events,
			Options = options,
		};
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await _pendingStorage.WriteBlobAsync(
				command.CommandId,
				_codec.EncodePendingBatch(batch),
				cancellationToken
			);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async IAsyncEnumerable<PendingCommandBatch> GetPendingAsync(
		  Guid streamId
		, [EnumeratorCancellation] CancellationToken cancellationToken = default
	)
	{
		foreach (var batch in await ReadPendingAsync(cancellationToken))
		{
			if (batch.Command.StreamId == streamId)
			{
				yield return batch;
			}
		}
	}

	public async IAsyncEnumerable<ConfirmedEventDigest> GetConfirmedDigestsAsync(
		  Guid streamId
		, [EnumeratorCancellation] CancellationToken cancellationToken = default
	)
	{
		foreach (var record in await ReadConfirmedRecordsAsync(cancellationToken))
		{
			var ev = _codec.DecodeConfirmedRecord(record.Data);
			if (ev.StreamId == streamId)
			{
				yield return new ConfirmedEventDigest(ev.EventId, _codec.HashConfirmedRecord(record.Data));
			}
		}
	}

	public async Task<ClientEventStoreChange> UpsertConfirmedAsync(Event ev, CancellationToken cancellationToken = default)
	{
		if (ev.StreamId == Guid.Empty)
		{
			throw new ArgumentException("A confirmed event requires a stream id.", nameof(ev));
		}

		await _gate.WaitAsync(cancellationToken);
		try
		{
			var hiddenByPendingBatch = await PendingExistsAsync(ev.CommandId, cancellationToken);
			var hasPendingTail = await HasPendingForStreamAsync(ev.StreamId, cancellationToken);
			var existing = await TryReadConfirmedAsync(ev.EventId, cancellationToken);
			var encoded = _codec.EncodeConfirmedRecord(ev);
			if (existing is not null && existing.AsSpan().SequenceEqual(encoded))
			{
				return ClientEventStoreChange.None;
			}
			await _confirmedStorage.WriteBlobAsync(
				ev.EventId,
				encoded,
				cancellationToken
			);
			if (hiddenByPendingBatch)
			{
				return ClientEventStoreChange.None;
			}
			if (hasPendingTail)
			{
				return ClientEventStoreChange.Rebuild;
			}
			return existing is null
				? ClientEventStoreChange.Append
				: ClientEventStoreChange.Rebuild;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<ClientEventStoreChange> DeleteConfirmedAsync(Guid eventId, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var record = await TryReadConfirmedAsync(eventId, cancellationToken);
			if (record is null)
			{
				return ClientEventStoreChange.None;
			}

			var ev = _codec.DecodeConfirmedRecord(record);
			var hiddenByPendingBatch = await PendingExistsAsync(ev.CommandId, cancellationToken);
			await _confirmedStorage.DeleteBlobAsync(eventId, cancellationToken);
			return hiddenByPendingBatch
				? ClientEventStoreChange.None
				: ClientEventStoreChange.Rebuild;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<ClientEventStoreChange> AcknowledgeAsync(Guid commandId, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var pending = await TryReadPendingAsync(commandId, cancellationToken);
			if (pending is null)
			{
				return ClientEventStoreChange.None;
			}
			var confirmedMatches = true;
			foreach (var optimisticEvent in pending.Events)
			{
				var confirmedRecord = await TryReadConfirmedAsync(
					optimisticEvent.EventId,
					cancellationToken
				);
				if (confirmedRecord is null
					|| !_codec.SerializeEvent(optimisticEvent).AsSpan().SequenceEqual(
						_codec.SerializeEvent(_codec.DecodeConfirmedRecord(confirmedRecord))
					)
				)
				{
					confirmedMatches = false;
					break;
				}
			}
			await _pendingStorage.DeleteBlobAsync(commandId, cancellationToken);
			return confirmedMatches
				? ClientEventStoreChange.None
				: ClientEventStoreChange.Rebuild;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task AppendAsync(Event item, CancellationToken cancellationToken = default)
	{
		await UpsertConfirmedAsync(item, cancellationToken);
	}

	public async Task AppendBatchAsync(IEnumerable<Event> items, CancellationToken cancellationToken = default)
	{
		foreach (var item in items)
		{
			await UpsertConfirmedAsync(item, cancellationToken);
		}
	}

	public async Task<Event> GetAsync(Guid key, CancellationToken cancellationToken = default)
	{
		await foreach (var ev in GetAllAsync(cancellationToken: cancellationToken))
		{
			if (ev.EventId == key)
			{
				return ev;
			}
		}
		throw new KeyNotFoundException($"Event {key} was not found in confirmed or pending client storage.");
	}

	public async IAsyncEnumerable<Event> GetAllAsync(
		  Guid from = default
		, [EnumeratorCancellation] CancellationToken cancellationToken = default
	)
	{
		var pending = await ReadPendingAsync(cancellationToken);
		var pendingCommandIds = pending.Select(x => x.Command.CommandId).ToHashSet();
		foreach (var record in await ReadConfirmedRecordsAsync(cancellationToken))
		{
			var ev = _codec.DecodeConfirmedRecord(record.Data);
			if (!pendingCommandIds.Contains(ev.CommandId)
				&& IsAtOrAfter(ev.EventId, from)
			)
			{
				yield return ev;
			}
		}
		foreach (var batch in pending)
		{
			foreach (var ev in batch.Events)
			{
				if (IsAtOrAfter(ev.EventId, from))
				{
					yield return ev;
				}
			}
		}
	}

	public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

	public void Dispose()
	{
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;

	private async Task<List<PendingCommandBatch>> ReadPendingAsync(CancellationToken cancellationToken)
	{
		var result = new List<PendingCommandBatch>();
		await foreach (var key in _pendingStorage.EnumerateKeysAsync(cancellationToken: cancellationToken))
		{
			var data = await _pendingStorage.ReadBlobAsync(key, cancellationToken);
			result.Add(_codec.DecodePendingBatch(data));
		}
		return result;
	}

	private async Task<List<ConfirmedRecord>> ReadConfirmedRecordsAsync(CancellationToken cancellationToken)
	{
		var result = new List<ConfirmedRecord>();
		await foreach (var key in _confirmedStorage.EnumerateKeysAsync(cancellationToken: cancellationToken))
		{
			result.Add(new ConfirmedRecord
			{
				EventId = key,
				Data = (await _confirmedStorage.ReadBlobAsync(key, cancellationToken)).ToArray(),
			});
		}
		return result;
	}

	private async Task<byte[]?> TryReadConfirmedAsync(Guid eventId, CancellationToken cancellationToken)
	{
		await foreach (var key in _confirmedStorage.EnumerateKeysAsync(cancellationToken: cancellationToken))
		{
			if (key == eventId)
			{
				return (await _confirmedStorage.ReadBlobAsync(key, cancellationToken)).ToArray();
			}
		}
		return null;
	}

	private async Task<bool> PendingExistsAsync(Guid commandId, CancellationToken cancellationToken)
	{
		await foreach (var key in _pendingStorage.EnumerateKeysAsync(cancellationToken: cancellationToken))
		{
			if (key == commandId)
			{
				return true;
			}
		}
		return false;
	}

	private async Task<bool> HasPendingForStreamAsync(
		  Guid streamId
		, CancellationToken cancellationToken
	)
	{
		await foreach (var key in _pendingStorage.EnumerateKeysAsync(cancellationToken: cancellationToken))
		{
			var batch = _codec.DecodePendingBatch(
				await _pendingStorage.ReadBlobAsync(key, cancellationToken)
			);
			if (batch.Command.StreamId == streamId)
			{
				return true;
			}
		}
		return false;
	}

	private async Task<PendingCommandBatch?> TryReadPendingAsync(
		  Guid commandId
		, CancellationToken cancellationToken
	)
	{
		await foreach (var key in _pendingStorage.EnumerateKeysAsync(cancellationToken: cancellationToken))
		{
			if (key == commandId)
			{
				return _codec.DecodePendingBatch(
					await _pendingStorage.ReadBlobAsync(key, cancellationToken)
				);
			}
		}
		return null;
	}

	private static bool IsAtOrAfter(Guid eventId, Guid from)
	{
		return from == Guid.Empty
			|| eventId.CompareTo(from) >= 0;
	}

	private sealed class ConfirmedRecord
	{
		public required Guid EventId { get; init; }
		public required byte[] Data { get; init; }
	}
}
