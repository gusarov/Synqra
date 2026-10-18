namespace Synqra.Tests.SampleModels.Serialization;

// a plain POCO: bool members go through the by-name (Level 3) path
public class SampleFlagPoco
{
	public bool Flag { get; set; }

	public bool? Maybe { get; set; }
}
