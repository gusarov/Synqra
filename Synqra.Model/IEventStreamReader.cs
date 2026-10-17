namespace Synqra;

/// <summary>
/// A log storage that reads one stream bounded to it (Mongo: <c>_sid ==</c> plus <c>_id &gt;</c> on the (_sid, _id) index), so a
/// read never touches, let alone deserializes, another stream's documents.
/// </summary>
public interface IEventStreamReader
{
	/// <summary>The stream's events strictly after <paramref name="after"/> (default: from its start), in sequence order.</summary>
	IAsyncEnumerable<Event> ReadStreamAsync(Guid streamId, Guid after = default, CancellationToken cancellationToken = default);
}
