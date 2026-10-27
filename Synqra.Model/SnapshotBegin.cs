namespace Synqra;

/// <summary>
/// Master → client: a stream snapshot follows — its chunks, then <see cref="SnapshotEnd"/>, then the backlog after
/// <see cref="Watermark"/>. This frame is the schema header: <see cref="Types"/> pins every type the payload uses
/// (plans/snapshot.md).
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A08-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.821, "1 FormatVersion long StreamId Guid SnapshotId Guid Watermark Guid TimeBase long Types IList<SnapshotType> EntityCount long")]
public partial class SnapshotBegin : TransportOperation
{
	/// <summary>Versions what the header cannot describe: the record layout and the snapshot frames.</summary>
	public required partial long FormatVersion { get; set; }

	public required partial Guid StreamId { get; set; }

	/// <summary>Names this snapshot on its chunks and its end.</summary>
	public required partial Guid SnapshotId { get; set; }

	/// <summary>The state is exactly the stream's state at this watermark.</summary>
	public required partial Guid Watermark { get; set; }

	/// <summary>The payload serializer's time base, in Unix seconds.</summary>
	public required partial long TimeBase { get; set; }

	public required partial IList<SnapshotType> Types { get; set; }

	/// <summary>The entities the master expects to send; <see cref="SnapshotEnd.RecordCount"/> is authoritative.</summary>
	public partial long EntityCount { get; set; }

	public override string ToString() => $"SnapshotBegin({StreamId} at {Watermark}, {Types?.Count} types, ~{EntityCount} entities)";
}
