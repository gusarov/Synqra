namespace Synqra.Tests.SampleModels;

/// <summary>Base of a small root hierarchy, to prove a base-typed collection sees derived roots.</summary>
[SynqraModel]
[Schema(2026.621, "1 Label string?")]
public partial class TestShapeBase
{
	public partial string? Label { get; set; }
}
