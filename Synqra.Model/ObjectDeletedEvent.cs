namespace Synqra;

// 9E02: staging registry, family E (event), local code 02 — the sibling slot next to
// ObjectPropertyChangedEvent (9E01) in the dying object/link vocabulary, which lives entirely in the
// staging registry. The node is all-zero because the id names a type.
[SynqraModel("C0DEADD0-1032-8000-9E02-000000000000")]
[SynqraLegacyTypeId("73117f2b-0223-5059-b1af-3a79facde03c", "2026-08-18", "id this type resolved to while it had no explicit [SynqraModel] id — it escaped the built-in guard because its namespace is exactly \"Synqra\", which StartsWith(\"Synqra.\") does not match; pinned on id assignment so events already persisted under it still resolve")]
public class ObjectDeletedEvent : SingleObjectEvent
{
	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
