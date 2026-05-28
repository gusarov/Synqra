using System;

namespace Synqra;

/// <summary>
/// Thrown by <see cref="IObjectStore.SubmitCommandAsync"/> when a
/// <see cref="CommandSubmissionOptions.ExpectedTargetVersion"/> precondition does not
/// match the projection's current version of the target object.
/// <para>
/// On the wire (HTTP) this typically maps to <c>412 Precondition Failed</c> with a body
/// carrying <see cref="ExpectedVersion"/> and <see cref="ActualVersion"/>.
/// </para>
/// </summary>
public sealed class ConcurrencyException : Exception
{
	public Guid TargetId { get; }
	public long ExpectedVersion { get; }
	public long ActualVersion { get; }

	public ConcurrencyException(Guid targetId, long expectedVersion, long actualVersion)
		: base($"Concurrency check failed for target {targetId}: expected version {expectedVersion}, actual {actualVersion}.")
	{
		TargetId = targetId;
		ExpectedVersion = expectedVersion;
		ActualVersion = actualVersion;
	}
}
