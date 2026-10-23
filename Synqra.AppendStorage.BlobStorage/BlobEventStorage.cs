using System.Text.Json;
using Synqra.BinarySerializer;
using Synqra.BlobStorage;

namespace Synqra.AppendStorage.BlobStorage;

/// <summary>
/// The event layer over <see cref="BlobAppendStorage{T, TKey}"/>: an event log's records, keyed by (stream, sequence).
/// When the blob store can append at its tail atomically (<see cref="ITailAppendBlobStorage{TKey}"/>: IndexedDB, which
/// every tab of a browser shares), each append stamps the next sequence after the store's tail and lands at most once per
/// <see cref="Event.EventId"/>, checked and written in one transaction. Tabs sharing the store then agree on one arrival
/// order and keep one copy of each event without talking to each other. Over any other blob store it appends as the base.
/// </summary>
public class BlobEventStorage : BlobAppendStorage<Event, (Guid Stream, Guid Sequence)>, IEventSequenceTail, IEventIdIndex
{
	readonly ITailAppendBlobStorage<(Guid Stream, Guid Sequence)>? _tailAppend;
	readonly IUniqueTagIndex? _tags;
	readonly GuidExtensions.Generator _stamps = new();

	public BlobEventStorage(IBlobStorage<(Guid Stream, Guid Sequence)> blobStorage, ISbxSerializerFactory serializerFactory, JsonSerializerOptions? jsonMirrorOptions = null)
		: base(blobStorage, serializerFactory, ev => (ev.StreamId, ev.SequenceId), jsonMirrorOptions)
	{
		_tailAppend = blobStorage as ITailAppendBlobStorage<(Guid Stream, Guid Sequence)>;
		_tags = _tailAppend as IUniqueTagIndex;
	}

	/// <summary>
	/// Over a tail-append store: stamps <see cref="Event.SequenceId"/> after the store's tail (whatever the caller proposed)
	/// and appends it there; when another writer moved the tail first, stamps again after the new one. An event whose
	/// EventId is stored already is not written again.
	/// </summary>
	public override async Task AppendAsync(Event item, CancellationToken cancellationToken = default)
	{
		if (_tailAppend is null)
		{
			await base.AppendAsync(item, cancellationToken);
		}
		else
		{
			var outcome = TailAppendResult.TailMoved;
			while (outcome == TailAppendResult.TailMoved)
			{
				var tail = await _tailAppend.ReadTailAsync(cancellationToken);
				_stamps.CreateVersion7OrApprove(tail.Sequence); // the clock follows the store's tail, whichever writer put it there
				item.SequenceId = _stamps.CreateVersion7();
				outcome = await _tailAppend.AppendAtTailAsync((item.StreamId, item.SequenceId), Serialize(item), JsonMirrorOf(item), item.EventId.ToString("N"), tail, cancellationToken);
			}
			if (outcome == TailAppendResult.Appended)
			{
				Attach(item);
			}
		}
	}

	/// <summary>
	/// The EventIds held: over a tail-append store they are its unique tags, read by key alone (one event's tag is its
	/// EventId); otherwise every event is read.
	/// </summary>
	public async IAsyncEnumerable<Guid> EnumerateEventIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		if (_tags is not null)
		{
			await foreach (var tag in _tags.EnumerateTagsAsync(cancellationToken))
			{
				yield return Guid.ParseExact(tag, "N");
			}
		}
		else
		{
			await foreach (var ev in GetAllAsync(default, cancellationToken))
			{
				yield return ev.EventId;
			}
		}
	}

	/// <summary>The stream's last sequence: one read of the store's tail when it can tell; otherwise a scan of the stream.</summary>
	public async Task<Guid> LastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default)
	{
		var last = default(Guid);
		if (_tailAppend is not null)
		{
			var tail = await _tailAppend.ReadTailAsync(cancellationToken);
			last = tail.Stream == streamId ? tail.Sequence : await ScanLastSequenceAsync(streamId, cancellationToken);
		}
		else
		{
			last = await ScanLastSequenceAsync(streamId, cancellationToken);
		}
		return last;
	}

	async Task<Guid> ScanLastSequenceAsync(Guid streamId, CancellationToken cancellationToken)
	{
		var last = default(Guid);
		await foreach (var ev in GetAllAsync((streamId, default), cancellationToken))
		{
			if (ev.StreamId == streamId && ev.SequenceId.CompareBigEndian(last) > 0)
			{
				last = ev.SequenceId;
			}
		}
		return last;
	}
}
