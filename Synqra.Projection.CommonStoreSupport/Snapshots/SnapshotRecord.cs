namespace Synqra;

/// <summary>
/// One entity record of a stream snapshot (plans/snapshot.md §3): a root, a facet or a link, with its typed data. A root is the
/// entity itself (<see cref="Owner"/> null); a facet or a link names the entity it hangs on.
/// </summary>
public sealed class SnapshotRecord
{
	/// <summary>The component's id; for a root, the entity's id.</summary>
	public required Guid ComponentId { get; init; }

	/// <summary>The entity a facet or a link belongs to (a link's source); null for a root.</summary>
	public Guid? Owner { get; init; }

	/// <summary>A root's version: the last event applied to the entity or any of its facets. Null on facets and links.</summary>
	public Guid? Version { get; init; }

	/// <summary>The component's data, of its own model type.</summary>
	public required object Data { get; init; }

	public bool IsRoot => Owner is null;

	public override string ToString() => IsRoot ? $"root {ComponentId} {Data.GetType().Name}" : $"{Data.GetType().Name} {ComponentId} of {Owner}";
}

/// <summary>
/// A stream's state taken at one watermark (plans/snapshot.md §4): the records are exactly the stream's entities as of
/// <see cref="Watermark"/>, roots first, then each owner's facets and links. Disposing it ends the read it holds open.
/// </summary>
public sealed class SnapshotCut : IAsyncDisposable
{
	readonly Func<ValueTask>? _dispose;

	public SnapshotCut(Guid watermark, IAsyncEnumerable<SnapshotRecord> records, Func<ValueTask>? dispose = null)
	{
		Watermark = watermark;
		Records = records;
		_dispose = dispose;
	}

	/// <summary>The stream's position the state is taken at; the backlog resumes strictly after it.</summary>
	public Guid Watermark { get; }

	public IAsyncEnumerable<SnapshotRecord> Records { get; }

	public ValueTask DisposeAsync() => _dispose?.Invoke() ?? ValueTask.CompletedTask;
}

/// <summary>
/// A master's view of a stream it can snapshot: its materialized state, cut consistently at one watermark. A host that registers
/// none declines every snapshot request, and a cold client replays the stream instead.
/// </summary>
public interface IStreamSnapshotSource
{
	/// <summary>The stream's state at its current watermark, or null when the stream holds nothing to snapshot.</summary>
	Task<SnapshotCut?> CutAsync(Guid streamId, CancellationToken cancellationToken = default);
}
