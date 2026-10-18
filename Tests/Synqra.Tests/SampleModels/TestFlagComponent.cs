namespace Synqra.Tests;

/// <summary>A component with a bool and a bool? member: SBX, BSON and JSON all have to bring both back exactly.</summary>
[SynqraModel]
[Component(IsUnique = true)]
[Schema(2026.794, "1 Done bool Pinned bool?")]
public partial class TestFlagComponent : IComponent
{
	public partial bool Done { get; set; }

	public partial bool? Pinned { get; set; }
}
