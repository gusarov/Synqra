namespace Synqra.Tests.SampleModels.Serialization;

// The layout an older build wrote: A, then B.
[SynqraModel("3D167E9C-FA0A-42EC-8A6D-2EE3D216CC71")]
[Schema(1.0, "1 A Guid B string?")]
public partial class SampleDroppedFieldWriterModel
{
	public partial Guid A { get; set; }
	public partial string? B { get; set; }
}
