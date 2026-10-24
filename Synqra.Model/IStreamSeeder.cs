namespace Synqra;

/// <summary>
/// Writes a brand-new stream's first objects. Synqra decides when, and only for a stream proven new (<see cref="StreamOrigin"/>):
/// a client that merely starts with an empty local log never runs one. The seeder decides what, and for which streams; one that
/// has nothing for a stream writes nothing. Its writes should be one command, so a failure leaves no partial seed behind.
/// </summary>
public interface IStreamSeeder
{
	Task SeedAsync(StreamSeedContext context, IObjectStore store);
}
