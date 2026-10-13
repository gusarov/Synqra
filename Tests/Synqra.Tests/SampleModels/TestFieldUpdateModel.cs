namespace Synqra.Tests.SampleModels;

/// <summary>Root model with non-string members, so a field-level Mongo write can be compared
/// element-by-element against a full ToDocument serialization of the same instance.</summary>
[SynqraModel]
[Schema(2026.620, "1 Name string? Weight double Tags IList<string>?")]
public partial class TestFieldUpdateModel
{
	public partial string? Name { get; set; }
	public partial double Weight { get; set; }
	public partial IList<string>? Tags { get; set; }
}
