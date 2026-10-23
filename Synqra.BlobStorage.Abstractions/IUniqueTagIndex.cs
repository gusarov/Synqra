namespace Synqra.BlobStorage;

/// <summary>
/// Optional capability of a tail-append store (<see cref="ITailAppendBlobStorage{TKey}"/>): the unique tags it holds, read by
/// key alone, no blob read or deserialized. An event log's tags are its EventIds, so a client learns what it holds without
/// reading its log. Check with an <c>is</c> pattern.
/// </summary>
public interface IUniqueTagIndex
{
	IAsyncEnumerable<string> EnumerateTagsAsync(CancellationToken cancellationToken = default);
}
