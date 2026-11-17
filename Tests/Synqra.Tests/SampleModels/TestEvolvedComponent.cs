namespace Synqra.Tests;

/// <summary>A component that gained members in a later schema, as Quotaly's IconComponent did (2026.500 → 2026.873).</summary>
// 9046: mode 9 (staging registry, pinned), family 0 (a plain domain model), local code 46; the node is all-zero because the id names a type.
[SynqraModel("C0DE0000-0000-8000-9046-000000000000")]
[Component(IsUnique = true)]
[Schema(2026.500, "1 Glyph string?")]
[Schema(2026.873, "1 Glyph string? IconKey string? IconId Guid")]
public partial class TestEvolvedComponent : IComponent
{
	public partial string? Glyph { get; set; }

	public partial string? IconKey { get; set; }

	public partial Guid IconId { get; set; }
}
