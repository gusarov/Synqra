namespace Synqra;

/// <summary>The stream a seeder is asked to fill, and how it was proven new.</summary>
public sealed class StreamSeedContext
{
	public required Guid StreamId { get; init; }

	public required StreamOrigin Origin { get; init; }
}
