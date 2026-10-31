namespace Synqra;

/// <summary>
/// A stream's state as of <see cref="SnapshotBegin.Watermark"/>, as the master sent it (plans/snapshot.md §5): a cold client keeps
/// it as the first record of its own log, so every load, cold or warm, folds it and then the events after it. It is the client's
/// own bookkeeping and never goes back to the master. <see cref="Chunks"/> are kept as received; <see cref="Header"/> pins the
/// schema they decode with.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A0D-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.831, "1 EventId Guid CommandId Guid SequenceId Guid Header SnapshotBegin Chunks IList<SnapshotChunk>")]
public partial class StreamSnapshotEvent : Event
{
	public required partial SnapshotBegin Header { get; set; }

	public required partial IList<SnapshotChunk> Chunks { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);

	public override string ToString() => $"StreamSnapshotEvent({Header?.StreamId} at {Header?.Watermark}, {Chunks?.Count} chunks)";
}
