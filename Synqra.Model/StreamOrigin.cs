namespace Synqra;

/// <summary>How a stream was proven new, the only two ways a seeder runs (docs/model.md §10).</summary>
public enum StreamOrigin
{
	/// <summary>This node created the stream: nobody else knows its id yet, so it is new, offline as well.</summary>
	CreatedHere = 1,

	/// <summary>The master found the stream's log empty, under the lock every seed of that stream takes.</summary>
	EmptyOnMaster = 2,

	/// <summary>The master found the stream holding events: a seed already there may be evolved, under the same lock.</summary>
	PopulatedOnMaster = 3,
}
