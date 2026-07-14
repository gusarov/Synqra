
namespace Synqra;

public interface IEventReplicationService
{
	bool IsOnline { get; }

	/// <summary>
	/// Raised when confirmed server events were appended to the logical projection sequence.
	/// </summary>
	event Action? EventsReceived;

	/// <summary>
	/// Raised when repair or command acknowledgement changed an existing logical sequence and the
	/// projection must be rebuilt before its active layer is replaced.
	/// </summary>
	event Action? RebuildRequired;

	Task StageAsync(
		  Command command
		, IReadOnlyList<Event> events
		, CommandSubmissionOptions? options = null
		, CancellationToken cancellationToken = default
	);

	/// <summary>
	/// Drops the current transport connection so changed credentials or endpoint configuration
	/// are applied by the next connection attempt.
	/// </summary>
	Task ReconnectAsync() => Task.CompletedTask;
}
