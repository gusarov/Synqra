namespace Synqra.AppendStorage;

/// <summary>
/// The owner of a local log that is only a cache of a server stream: drops a log it cannot read, keeps what it has not
/// handed to the server yet, and fetches the stream again.
/// </summary>
public interface ILocalLogRecovery
{
	Task RecoverLocalLogAsync(UnreadableRecordException cause, CancellationToken cancellationToken = default);
}
