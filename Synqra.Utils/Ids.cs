using System;
using System.Threading;

namespace Synqra;

/// <summary>
/// Domain-specific id factory. Production code mints ids through these role-named entry points
/// (<see cref="CreateCommandId"/>, <see cref="CreateEventId"/>, …) rather than
/// <see cref="GuidExtensions"/> directly, so a test host can switch to predictable, per-type,
/// monotonic ids — the <b>A</b> test-auto variant (see docs/model.md §8) — without touching
/// <see cref="GuidExtensions"/> (whose own tests stay on real v7).
/// <para>
/// Static, so it reaches background-service threads and WAF hosts (an ambient <c>AsyncLocal</c> would
/// not). Toggle <see cref="DeterministicTestIds"/> once at startup (test module initializer, or app
/// config) — default is production (real v7).
/// </para>
/// </summary>
public static class Ids
{
	/// <summary>
	/// When set, <c>Create*Id()</c> return predictable per-class <b>A</b>-variant ids
	/// (<c>C0DE0000-0000-8000-A{class}-{counter}</c>) instead of random v7. Set once at startup.
	/// </summary>
	public static bool DeterministicTestIds { get; set; }

	// One counter per class (not per method) so component + link ids (both class 001) never collide.
	// Spaced by 0x100 so a command's derived events (CreateEventId = CommandId + ordinal) fill the low
	// byte without hitting the next minted id.
	static long _command, _stream, _componentClass;

	/// <summary>A command id (class <c>00C</c>).</summary>
	public static Guid CreateCommandId() => New(0x00C, ref _command);

	/// <summary>A container / stream id (class <c>005</c>).</summary>
	public static Guid CreateStreamId() => New(0x005, ref _stream);

	/// <summary>An entity / root-component id (class <c>001</c>).</summary>
	public static Guid CreateComponentId() => New(0x001, ref _componentClass);

	/// <summary>A link id — links are components, so class <c>001</c> (shares the component counter).</summary>
	public static Guid CreateLinkId() => New(0x001, ref _componentClass);

	/// <summary>
	/// The id of the <paramref name="ordinal"/>-th event a command expands to: <c>Derive(commandId,
	/// ordinal) = commandId + ordinal</c>. Events inherit their command's variant/class, so there is no
	/// separate counter — a test-auto command's events are automatically test-auto too.
	/// </summary>
	public static Guid CreateEventId(Guid commandId, int ordinal) => GuidExtensions.Derive(commandId, ordinal);

	static Guid New(ushort @class, ref long counter) =>
		DeterministicTestIds
			? new Guid($"C0DE0000-0000-8000-A{@class:X3}-{Interlocked.Add(ref counter, 0x100):X12}")
			: GuidExtensions.CreateVersion7();
}
