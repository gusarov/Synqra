namespace Synqra;

/// <summary>A seeder failed on a stream proven new. Whatever it committed before failing stays, so the stream is not seeded again.</summary>
public sealed class StreamSeedingException : Exception
{
	public StreamSeedingException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
