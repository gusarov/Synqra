namespace Synqra;

/// <summary>
/// One type a snapshot's payload uses, pinned: the SBX id the payload writes for it, the Synqra type id that names it on any
/// build, and the exact schema its records were written at. The reader maps every entry before it decodes a chunk and refuses
/// the whole snapshot when it lacks one (plans/snapshot.md §2).
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A0C-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.821, "1 MapId long TypeId Guid Version double Schema string?")]
public sealed partial class SnapshotType
{
	/// <summary>The SBX id the payload writes for this type.</summary>
	public required partial long MapId { get; set; }

	/// <summary>The Synqra type id: explicit, or derived from the full name.</summary>
	public required partial Guid TypeId { get; set; }

	/// <summary>The schema version the records were written at.</summary>
	public required partial double Version { get; set; }

	/// <summary>The schema signature of that version; null for a type with none (a binder-backed or inherited layout).</summary>
	public partial string? Schema { get; set; }

	public override string ToString() => $"{MapId} {TypeId} {Version} \"{Schema}\"";
}
