namespace Synqra.Tests;

/// <summary>A component only ever read back from a hand-built document: the driver never serializes it, so it never learns its <c>_t</c>.</summary>
[SynqraModel]
[Component(IsUnique = true)]
[Schema(2026.788, "1 Subject string?")]
public partial class TestColdComponent : IComponent
{
	public partial string? Subject { get; set; }
}
