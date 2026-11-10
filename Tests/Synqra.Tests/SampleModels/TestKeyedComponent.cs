namespace Synqra.Tests;

/// <summary>A non-unique component with an indexed key and an owner: what a host resolves by value, never by scanning a collection.</summary>
// 9045: mode 9 (staging registry, pinned), family 0 (a plain domain model), local code 45; the node is all-zero because the id names a type.
[SynqraModel("C0DE0000-0000-8000-9045-000000000000")]
[Schema(2026.859, "1 Key string? Owner Guid")]
public partial class TestKeyedComponent : IComponent
{
	[Indexed]
	public partial string? Key { get; set; }

	public partial Guid Owner { get; set; }
}
