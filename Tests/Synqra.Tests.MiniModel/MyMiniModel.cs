using Synqra;

namespace Synqra.Tests.MiniModel;

[Schema(2025.791, "1 Subject string? Number int Id int")]
[Schema(2025.792, "1 Id int")]
[Schema(2025.793, "1 Id int Subject string? Number int")]
[Schema(2025.794, "1 Id int")]
[Schema(2025.795, "1 Id int Subject string? Number int")]
public partial class MyMiniModel : MyBaseModel
{
	public partial string? Subject { get; set; }
	public partial int Number { get; set; }
}

[Schema(2025.792, "1 Id int")]
[Schema(2025.793, "1")]
[Schema(2025.794, "1 Id int")]
public partial class MyBaseModel
{
	public partial int Id { get; set; }
}
