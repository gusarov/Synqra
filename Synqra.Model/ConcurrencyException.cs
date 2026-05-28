using System;

namespace Synqra;

/// <summary>
/// Thrown by <see cref="IObjectStore.SubmitCommandAsync"/> when a
/// <see cref="CommandSubmissionOptions.ExpectedLastEventId"/> precondition does not
/// match the projection's current last-applied event id for the target object.
/// <para>
/// On the wire (HTTP) this typically maps to <c>412 Precondition Failed</c> with a body
/// carrying <see cref="ExpectedLastEventId"/> and <see cref="ActualLastEventId"/>.
/// </para>
/// </summary>
public sealed class ConcurrencyException : Exception
{
	public Guid TargetId { get; }
	public Guid ExpectedLastEventId { get; }
	public Guid ActualLastEventId { get; }

	public ConcurrencyException(Guid targetId, Guid expectedLastEventId, Guid actualLastEventId)
		: base($"Concurrency check failed for target {targetId}: expected last event id {expectedLastEventId}, actual {actualLastEventId}.")
	{
		TargetId = targetId;
		ExpectedLastEventId = expectedLastEventId;
		ActualLastEventId = actualLastEventId;
	}
}
