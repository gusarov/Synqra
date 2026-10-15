namespace Synqra.Tests.SampleModels.TwinA;

/// <summary>Shares its simple name with <see cref="TwinB.Twin"/>: two features of one host each having a <c>Node</c>.</summary>
[SynqraModel]
[Schema(2026.786, "1 Name string?")]
public partial class Twin
{
	public partial string? Name { get; set; }
}
