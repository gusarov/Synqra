using System;

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
	/// Optimistic concurrency precondition — content-addressed, in the spirit of
	/// <c>git push --force-with-lease=&lt;sha&gt;</c>.
	/// <para>
	/// When non-default and the command is a <see cref="SingleObjectCommand"/>,
	/// the projection checks that the target object's last applied event id equals
	/// this value. Mismatch throws <see cref="ConcurrencyException"/>; no events
	/// are produced.
	/// </para>
	/// <para>
	/// <see cref="Guid.Empty"/> means "I don't care" — preserves the historical
	/// last-writer-wins behaviour and is what manually-constructed commands get
	/// when no options are passed.
	/// </para>
	/// <para>
	/// Why an event id rather than a numeric version: a per-target counter requires
	/// the caller and the projection to agree on event ordering and count, which is
	/// fragile under replication races. An event id is self-contained — it names a
	/// specific point in history, and either the projection has applied exactly up
	/// to that point or it has not.
	/// </para>
	/// </summary>
	public Guid ExpectedLastEventId { get; set; }
}
