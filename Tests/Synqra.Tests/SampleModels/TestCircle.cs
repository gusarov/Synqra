namespace Synqra.Tests.SampleModels;

[SynqraModel]
[Schema(2026.621, "1 Label string? Radius double")]
public partial class TestCircle : TestShapeBase
{
	public partial double Radius { get; set; }
}
