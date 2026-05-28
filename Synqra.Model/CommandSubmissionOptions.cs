using System.Threading;

namespace Synqra;

/// <summary>
/// Per-call options for <see cref="IObjectStore.SubmitCommandAsync"/>.
/// These are <i>request-side</i> concerns — they affect how the command is validated
/// and dispatched but are <b>not</b> persisted in the event stream.
/// </summary>
/// <remarks>
/// Designed as an extensible bag so future submission-time concerns
/// (idempotency keys, trace ids, authorization contexts, deadlines)
/// can be added without breaking the <see cref="IObjectStore"/> contract.
/// </remarks>
public sealed class CommandSubmissionOptions
{
	/// <summary>
	/// Optimistic concurrency precondition. When non-null and the command is a
	/// <see cref="SingleObjectCommand"/>, the projection checks that the target object's
	/// current version equals this value. Mismatch throws <see cref="ConcurrencyException"/>;
	/// no events are produced.
	/// <para>
	/// Null preserves the historical "last-writer-wins" behaviour.
	/// </para>
	/// </summary>
	public long? ExpectedTargetVersion { get; set; }
}
