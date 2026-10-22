namespace Synqra;

/// <summary>
/// A sequenced server log's per-stream watermark: the highest committed <see cref="Event.SequenceId"/> below which
/// nothing is still being committed. A client that holds everything up to it can resume from it (docs/model.md §9).
/// </summary>
public interface IEventWatermarks
{
	/// <summary>The watermark as this process knows it: default for a stream it has neither stamped nor seeded since it started.</summary>
	Guid ReadWatermark(Guid streamId);

	/// <summary>
	/// The watermark, seeded from the log's tail first when this process has not touched the stream yet: a resume point
	/// handed out before the first append is then a real position, not an empty one that no client can move to.
	/// </summary>
	ValueTask<Guid> ReadWatermarkAsync(Guid streamId, CancellationToken cancellationToken = default) => new(ReadWatermark(streamId));
}
