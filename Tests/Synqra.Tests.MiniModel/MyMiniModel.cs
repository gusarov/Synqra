using Synqra;

namespace Synqra.Tests.MiniModel;

[Schema(2025.08, "0 Subject str? Number zig")]
[Schema(2025.772, "1 Subject string? Number int Number2 int")]
[Schema(2025.773, "1 Subject string? Number int Number2 int Number3 int")]
[Schema(2025.774, "1 Subject string? Number int Number2 int Number3 int Number4 int")]
[Schema(2025.775, "1 Subject string? Subject2 string? Number int Number2 int Number3 int Number4 int")]
[Schema(2025.776, "1 Subject string? Number inta")]
[Schema(2025.790, "1 Subject string? Number int")]
public partial class MyMiniModel
{
	public partial string? Subject { get; set; }
	public partial int Number { get; set; }
}
