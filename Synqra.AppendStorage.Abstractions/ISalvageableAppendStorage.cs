namespace Synqra.AppendStorage;

/// <summary>A storage that reads past what it cannot read: every readable record, each unreadable one reported instead.</summary>
public interface ISalvageableAppendStorage<T>
	where T : class
{
	IAsyncEnumerable<T> SalvageAsync(Action<UnreadableRecordException> unreadable, CancellationToken cancellationToken = default);
}
