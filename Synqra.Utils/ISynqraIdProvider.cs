using System;
using System.Threading;

namespace Synqra;

/// <summary>
/// Domain-specific id factory. Production code mints ids through these role-named entry points
/// (<see cref="CreateCommandId"/>, <see cref="CreateEventId"/>, …) rather than
/// <see cref="GuidExtensions"/> directly, so a test host can inject a deterministic implementation
/// (<see cref="DeterministicSynqraIdProvider"/>) without touching <see cref="GuidExtensions"/>
/// (whose own tests stay on real v7).
/// <para>
/// Injected via DI — every mint site that needs one either receives it (projections/stores) or has
/// the store fill the id on submit (commands, components). There is no ambient static factory.
/// </para>
/// </summary>
public interface ISynqraIdProvider
{
	/// <summary>A command id (class <c>00C</c>).</summary>
	Guid CreateCommandId();

	/// <summary>A container / stream id (class <c>005</c>).</summary>
	Guid CreateStreamId();

	/// <summary>An entity / root-component id (class <c>001</c>).</summary>
	Guid CreateComponentId();

	/// <summary>A collection id (class <c>002</c>). Reserved: collections are being retired, kept
	/// documented/compliant while still in the codebase.</summary>
	Guid CreateCollectionId();

	/// <summary>A link id (class <c>003</c>). Reserved: links are being folded into components, kept
	/// documented/compliant while still in the codebase.</summary>
	Guid CreateLinkId();

	/// <summary>
	/// The id of the <paramref name="ordinal"/>-th event a command expands to:
	/// <c>Derive(commandId, ordinal) = commandId + ordinal</c>. Events inherit their command's
	/// variant/class, so a deterministic command's events are deterministic too.
	/// </summary>
	Guid CreateEventId(Guid commandId, int ordinal);
}

/// <summary>
/// Production <see cref="ISynqraIdProvider"/> — random, monotonic v7 ids. Stateless and thread-safe;
/// registered as the default singleton by the store DI extensions.
/// </summary>
public sealed class SynqraIdProvider : ISynqraIdProvider
{
	/// <summary>Shared instance for the rare non-DI construction path (e.g. <c>new InMemoryProjection(...)</c>).</summary>
	public static readonly SynqraIdProvider Default = new();

	public Guid CreateCommandId() => GuidExtensions.CreateVersion7();
	public Guid CreateStreamId() => GuidExtensions.CreateVersion7();
	public Guid CreateComponentId() => GuidExtensions.CreateVersion7();
	public Guid CreateCollectionId() => GuidExtensions.CreateVersion7();
	public Guid CreateLinkId() => GuidExtensions.CreateVersion7();
	public Guid CreateEventId(Guid commandId, int ordinal) => GuidExtensions.Derive(commandId, ordinal);
}

/// <summary>
/// Test <see cref="ISynqraIdProvider"/> — predictable, per-class monotonic <b>A</b> test-auto variant
/// ids (<c>C0DE0000-0000-8000-A{class}-{counter}</c>, see docs/model.md §8) so ids minted by
/// production code under test read cleanly in logs and assertions. Registered by the test host in
/// place of <see cref="SynqraIdProvider"/>; being a DI singleton it is shared across background-service
/// threads and WAF hosts.
/// </summary>
public sealed class DeterministicSynqraIdProvider : ISynqraIdProvider
{
	// One counter per class. Only the command counter strides by 0x100, to reserve the low byte for a
	// command's derived events (CreateEventId = CommandId + ordinal); every other class steps by 1.
	long _command, _stream, _component, _collection, _link;

	public Guid CreateCommandId() => New(0x00C, ref _command, 0x100);
	public Guid CreateStreamId() => New(0x005, ref _stream, 1);
	public Guid CreateComponentId() => New(0x001, ref _component, 1);
	public Guid CreateCollectionId() => New(0x002, ref _collection, 1);
	public Guid CreateLinkId() => New(0x003, ref _link, 1);
	public Guid CreateEventId(Guid commandId, int ordinal) => GuidExtensions.Derive(commandId, ordinal);

	static Guid New(ushort @class, ref long counter, long step) =>
		new Guid($"C0DE0000-0000-8000-A{@class:X3}-{Interlocked.Add(ref counter, step):X12}");
}
