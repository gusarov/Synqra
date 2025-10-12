namespace Synqra.Tests.BinarySerialization;

[Schema(2025.09, "0 Id int Name str?")]
[Schema(2025.772, "1 Id int Name string?")]
public partial class SampleTestSynqraModel
{
	public partial int Id { get; set; }
	public partial string? Name { get; set; }
}
