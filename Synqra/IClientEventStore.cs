using Synqra.AppendStorage;

namespace Synqra;

public sealed class PendingCommandBatch
{
	public required Command Command { get; init; }
	public required IReadOnlyList<Event> Events { get; init; }
	public CommandSubmissionOptions? Options { get; init; }
}

public readonly record struct ConfirmedEventDigest(Guid EventId, uint Hash);

public enum ClientEventStoreChange
{
	None,
	Append,
	Rebuild,
}

/// <summary>
/// Durable client-side event storage. Confirmed server records and optimistic command batches are
/// physically separate; the inherited append-storage view reads confirmed records followed by the
/// still-pending optimistic events.
/// </summary>
public interface IClientEventStore : IAppendStorage<Event, Guid>
{
	Task StageAsync(
		  Command command
		, IReadOnlyList<Event> events
		, CommandSubmissionOptions? options = null
		, CancellationToken cancellationToken = default
	);

	IAsyncEnumerable<PendingCommandBatch> GetPendingAsync(
		  Guid streamId
		, CancellationToken cancellationToken = default
	);

	IAsyncEnumerable<ConfirmedEventDigest> GetConfirmedDigestsAsync(
		  Guid streamId
		, CancellationToken cancellationToken = default
	);

	Task<ClientEventStoreChange> UpsertConfirmedAsync(Event ev, CancellationToken cancellationToken = default);

	Task<ClientEventStoreChange> DeleteConfirmedAsync(Guid eventId, CancellationToken cancellationToken = default);

	Task<ClientEventStoreChange> AcknowledgeAsync(Guid commandId, CancellationToken cancellationToken = default);
}
