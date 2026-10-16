namespace Synqra;

/// <summary>A server log that can report a stream's highest committed <see cref="Event.SequenceId"/> cheaply (Mongo: one indexed query).</summary>
public interface IEventSequenceTail
{
	/// <summary>The highest <see cref="Event.SequenceId"/> the log holds for the stream, or default when it holds none.</summary>
	Task<Guid> LastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default);
}
