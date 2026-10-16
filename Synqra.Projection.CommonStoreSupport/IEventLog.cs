using System.Runtime.CompilerServices;
using Synqra.AppendStorage;

namespace Synqra;

/// <summary>
/// An event log: an append storage keyed by (stream, sequence), where the sequence is the event's position in THIS log,
/// stamped on append (docs/model.md §9). A stream is a key prefix. A read after a (stream, cursor) key runs on into later
/// streams, so <see cref="ReadStreamAsync"/> stops at the first event of another one.
/// </summary>
public interface IEventLog : IAppendStorage<Event, (Guid Stream, Guid Sequence)>
{
	/// <summary>One stream's events strictly after <paramref name="after"/> (default: from its start), in log order.</summary>
	async IAsyncEnumerable<Event> ReadStreamAsync(Guid streamId, Guid after = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var inStream = true;
		await using var events = GetAllAsync((streamId, after), cancellationToken).GetAsyncEnumerator(cancellationToken);
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
}
