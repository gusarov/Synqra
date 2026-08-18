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
	/// <summary>
	/// A command instance id. <paramref name="commandTypeId"/> is the submitted command's own
	/// <c>[SynqraModel]</c> type id; a structured id carries its <c>Cxx</c> class so the instance names
	/// the concrete command it is (there is no generic "some command" class). Providers that mint opaque
	/// ids (production v7) ignore it.
	/// </summary>
	Guid CreateCommandId(Guid commandTypeId);

	/// <summary>A container / stream id (instance class <c>005</c>).</summary>
	Guid CreateStreamId();

	/// <summary>An entity / root-component id (instance class <c>001</c>).</summary>
	Guid CreateComponentId();

	/// <summary>A collection id (instance class <c>002</c>). Reserved: collections are being retired, kept
	/// documented/compliant while still in the codebase.</summary>
	Guid CreateCollectionId();

	/// <summary>A link id (instance class <c>003</c>). Reserved: links are being folded into components, kept
	/// documented/compliant while still in the codebase.</summary>
	Guid CreateLinkId();

	/// <summary>
	/// The id of the <paramref name="ordinal"/>-th event a command expands to. Delegates to
	/// <see cref="GuidExtensions.DeriveEventId"/>: opaque (v7) command ids advance by
	/// <paramref name="ordinal"/> unchanged, while a structured command id additionally takes the
	/// <c>Exx</c> class of <paramref name="eventTypeId"/> so each derived event names its own event type
	/// even when one command expands to several different ones.
	/// </summary>
	Guid CreateEventId(Guid commandId, Guid eventTypeId, int ordinal);
}

/// <summary>
/// Production <see cref="ISynqraIdProvider"/> — random, monotonic v7 ids. Stateless and thread-safe;
/// registered as the default singleton by the store DI extensions.
/// </summary>
public sealed class SynqraIdProvider : ISynqraIdProvider
{
	/// <summary>Shared instance for the rare non-DI construction path (e.g. <c>new InMemoryProjection(...)</c>).</summary>
	public static readonly SynqraIdProvider Default = new();

	public Guid CreateCommandId(Guid commandTypeId) => GuidExtensions.CreateVersion7();
	public Guid CreateStreamId() => GuidExtensions.CreateVersion7();
	public Guid CreateComponentId() => GuidExtensions.CreateVersion7();
	public Guid CreateCollectionId() => GuidExtensions.CreateVersion7();
	public Guid CreateLinkId() => GuidExtensions.CreateVersion7();
	public Guid CreateEventId(Guid commandId, Guid eventTypeId, int ordinal) => GuidExtensions.DeriveEventId(commandId, eventTypeId, ordinal);
}

/// <summary>
/// Test <see cref="ISynqraIdProvider"/> — predictable, per-class monotonic <b>A</b>-stage ids
/// (<c>C0DE0000-0000-8000-A{class}-{counter}</c>, see docs/model.md §8) so ids minted by production
/// code under test read cleanly in logs and assertions. Registered by the test host in place of
/// <see cref="SynqraIdProvider"/>; being a DI singleton it is shared across background-service threads
/// and WAF hosts.
/// </summary>
public sealed class DeterministicSynqraIdProvider : ISynqraIdProvider
{
	// One counter per class. Only the command counter strides by 0x100, to reserve the low byte for a
	// command's derived events (CreateEventId = command node + ordinal); every other class steps by 1.
	// The command counter is shared across command types on purpose: two command types get different
	// classes, but a single striding counter also keeps their nodes distinct and chronologically readable.
	long _command, _stream, _component, _collection, _link;

	public Guid CreateCommandId(Guid commandTypeId) => New(ClassOf(commandTypeId, family: 0xC), ref _command, 0x100);
	public Guid CreateStreamId() => New(0x005, ref _stream, 1);
	public Guid CreateComponentId() => New(0x001, ref _component, 1);
	public Guid CreateCollectionId() => New(0x002, ref _collection, 1);
	public Guid CreateLinkId() => New(0x003, ref _link, 1);
	public Guid CreateEventId(Guid commandId, Guid eventTypeId, int ordinal) => GuidExtensions.DeriveEventId(commandId, eventTypeId, ordinal);

	/// <summary>
	/// The class an instance of <paramref name="typeId"/> should carry. A structured type id already owns
	/// a family + code, so it is taken verbatim. A type with no structured id (a consumer type whose
	/// <c>[SynqraModel]</c> is parameterless, so its id is a v5 hash) has no registered code; one is
	/// synthesised from the hash into the high half <c>0x80..0xFF</c> — stable across runs, never
	/// <c>00</c> (which would read as the family's base type), and visibly "unregistered" at a glance.
	/// </summary>
	static unsafe ushort ClassOf(Guid typeId, int family)
	{
		if (typeId.IsStructuredId())
		{
			return typeId.GetStructuredClass();
		}
		byte* b = (byte*)&typeId;
		return (ushort)((family << 8) | (b[15] | 0x80));
	}

	static Guid New(ushort @class, ref long counter, long step) =>
		new Guid($"C0DE0000-0000-8000-A{@class:X3}-{Interlocked.Add(ref counter, step):X12}");
}
