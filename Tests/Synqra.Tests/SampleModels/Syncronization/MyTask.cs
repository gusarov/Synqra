// This is a primary test model with generated code
using Synqra.BinarySerializer;

namespace Synqra.Tests.SampleModels.Syncronization;

[SynqraModel]
[Schema(2025.08, "1 Subject str? Number zig")]
[Schema(2025.09, "1 Subject str? Number zig2")]
[Schema(2025.77, "1 Subject string? Number int")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 Subject string? Number int")]
[Schema(2025.793, "1")]
[Schema(2025.794, "1 Subject string? Number int")]
[Schema(2026.156, "1 Subject string? Number int")]
public partial class SampleTaskModel
{
	public partial string? Subject { get; set; }
	public partial int Number { get; set; }
}
