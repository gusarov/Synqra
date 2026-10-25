namespace Synqra;

/// <summary>What the master's seeding did for a stream.</summary>
public enum StreamSeedOutcome
{
	/// <summary>Nothing to do: no seeder, the stream settled earlier, or its seed is current.</summary>
	None = 0,

	/// <summary>The stream was empty and is seeded now.</summary>
	Seeded = 1,

	/// <summary>The stream held an older seed and is evolved to the current version now.</summary>
	Upgraded = 2,
}
