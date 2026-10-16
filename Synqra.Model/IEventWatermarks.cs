namespace Synqra;

/// <summary>
/// A sequenced server log's per-stream watermark: the highest committed <see cref="Event.SequenceId"/> below which
/// nothing is still being committed. A client that holds everything up to it can resume from it (docs/model.md §9).
/// </summary>
public interface IEventWatermarks
{
	Guid ReadWatermark(Guid streamId);
}
