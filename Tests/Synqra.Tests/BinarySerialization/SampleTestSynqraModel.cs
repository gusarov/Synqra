namespace Synqra.Tests.BinarySerialization;

[Schema(2025.09, "0 Id int Name str?")]
[Schema(2025.772, "1 Id int Name string?")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Id int Name string?")]
[Schema(2025.793, "1")]
[Schema(2025.794, "1 Id int Name string?")]
public partial class SampleTestSynqraModel
{
	public partial int Id { get; set; }
	public partial string? Name { get; set; }
}
