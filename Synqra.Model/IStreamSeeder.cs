namespace Synqra;

/// <summary>
/// Writes a brand-new stream's first objects, and evolves them as the seed grows. Synqra decides when, and only on proof
/// (<see cref="StreamOrigin"/>): a client that merely starts with an empty local log never runs one. The seeder decides what, and
/// for which streams; one that has nothing for a stream writes nothing. Its writes should be one command, so a failure leaves no
/// partial seed behind.
/// <para>
/// A seed evolves (docs/model.md §10): <see cref="SeedAsync"/> writes the current <see cref="Version"/>; the master asks a stream
/// that holds events which version its seed is at (<see cref="ReadVersionAsync"/>, from the seeder's own objects) and applies the
/// missing steps (<see cref="UpgradeAsync"/>), once, under the same lock as the seed.
/// </para>
/// </summary>
public interface IStreamSeeder
{
	Task SeedAsync(StreamSeedContext context, IObjectStore store);

	/// <summary>The version <see cref="SeedAsync"/> writes and <see cref="UpgradeAsync"/> brings a stream to. 1 until the seed evolves.</summary>
	int Version => 1;

	/// <summary>
	/// The version this stream's seed is at, read from the seeder's own objects; null when the stream is not this seeder's or holds
	/// nothing it seeded, which leaves the stream alone.
	/// </summary>
	Task<int?> ReadVersionAsync(StreamSeedContext context, IObjectStore store) => Task.FromResult<int?>(null);

	/// <summary>Brings a stream seeded at <paramref name="fromVersion"/> to <see cref="Version"/>, recording the version where <see cref="ReadVersionAsync"/> reads it.</summary>
	Task UpgradeAsync(StreamSeedContext context, IObjectStore store, int fromVersion) => Task.CompletedTask;
}
