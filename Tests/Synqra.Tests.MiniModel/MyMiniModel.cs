using Synqra;

namespace Synqra.Tests.MiniModel;

[Schema(2025.08, "0 Subject str? Number zig")]
public partial class MyMiniModel
{
	public partial string? Subject { get; set; }
	public partial int Number { get; set; }
	public partial int Number2 { get; set; }
}
