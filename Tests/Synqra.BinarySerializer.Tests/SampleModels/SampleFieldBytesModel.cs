namespace Synqra.Tests.SampleModels.Serialization;

[SynqraModel]
[Schema(2026.821, "1 Data byte[]?")]
public partial class SampleFieldBytesModel
{
	public partial byte[]? Data { get; set; }
}
