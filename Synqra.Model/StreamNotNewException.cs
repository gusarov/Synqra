namespace Synqra;

/// <summary>A creator seed was asked for a stream whose log already holds events: it was not created here, or was seeded already.</summary>
public sealed class StreamNotNewException : InvalidOperationException
{
	public StreamNotNewException(Guid streamId)
		: base($"Stream {streamId} already holds events: it was not created here, and only a stream's creator or its master seeds it.")
	{
		StreamId = streamId;
	}

	public Guid StreamId { get; }
}
