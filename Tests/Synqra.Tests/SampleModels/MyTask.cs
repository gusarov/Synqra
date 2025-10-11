// This is a primary test model with generated code
using Synqra;

namespace Synqra.Tests.SampleModels;

[Schema(2025.08, "1 Subject str? Number zig")]
[Schema(2025.09, "1 Subject str? Number zig2")]
[Schema(2025.77, "1 Subject string? Number int")]
public partial class MyTaskModel
{
	public partial string? Subject { get; set; }
	public partial int Number { get; set; }
}
