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

	/// <summary>A container / stream id (semantic class <c>005</c> — family <c>0</c>, code <c>05</c>).</summary>
	Guid CreateStreamId();

	/// <summary>An entity / root-component id (semantic class <c>001</c> — family <c>0</c>, code <c>01</c>).</summary>
	Guid CreateComponentId();

	/// <summary>A collection id (semantic class <c>002</c>).</summary>
	[Obsolete("Collections are retired — the vocabulary is being folded into components. No production code mints a collection id; this remains only so the still-present collection tests keep compiling.")]
	Guid CreateCollectionId();

	/// <summary>A link id (semantic class <c>003</c>). Reserved: links are being folded into components, kept
	/// documented/compliant while still in the codebase.</summary>
	Guid CreateLinkId();

	/// <summary>
	/// The id of the <paramref name="ordinal"/>-th event a command expands to.
	/// </summary>
	[Obsolete("The derivation is identical for every provider, so it is not a provider-varying operation: call SynqraIdDerivation.DeriveEventId(commandId, eventTypeId, ordinal), or the CreateEventId<TEvent>(commandId, ordinal) extension which resolves the event type id for you.")]
	Guid CreateEventId(Guid commandId, Guid eventTypeId, int ordinal);
}

/// <summary>
/// How a command's id expands into the ids of the events it produces. This is id-provider logic, not a
/// property of the UUID layout, so it lives beside <see cref="ISynqraIdProvider"/> rather than in
/// <see cref="GuidExtensions"/> (RFC 9562 only) or <see cref="CodeGuidExtensions"/> (the CODE v8 bit
/// layout, which deliberately knows nothing about commands or events).
/// <para>
/// It is a static rather than a member of <see cref="ISynqraIdProvider"/> because the derivation is
/// provider-independent — the same command expands to the same event ids whichever provider minted it —
/// so every implementation would otherwise repeat it identically. (A static interface member would say
/// this more directly, but <c>Synqra.Utils</c> still targets netstandard2.0, which has none.)
/// </para>
/// </summary>
public static class SynqraIdDerivation
{
	/// <summary>
	/// Deterministically derives the id of the <paramref name="ordinal"/>-th event a command expands
	/// to, from an <b>opaque</b> command id (production: a client-generated v7). This makes the whole
	/// command→event expansion reproducible across nodes and replays (core.md §8: same command ⇒ same
	/// events) with no clock or shared counter. Modelled on the Todo predecessor's id layout (a reserved
	/// low-bytes counter region + increment): the low 56 random bits are incremented by
	/// <paramref name="ordinal"/> while the timestamp, version and variant bytes are preserved, so the
	/// result stays a valid, time-ordered v7 that sorts adjacent to its command.
	/// </summary>
	public static unsafe Guid Derive(Guid commandId, int ordinal)
	{
		if (ordinal < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(ordinal));
		}
		byte* b = (byte*)&commandId;
		// Increment the trailing 56 random bits (bytes 9..15, big-endian). Bytes 0..8 (timestamp +
		// version nibble + variant) are untouched, so the derived id is still a valid v7 sharing the
		// command's time position; ordinals stay far below the 56-bit space for any real command.
		ulong low = 0;
		for (int i = 9; i < 16; i++)
		{
			low = (low << 8) | b[i];
		}
		low += (ulong)ordinal;
		for (int i = 15; i >= 9; i--)
		{
			b[i] = (byte)low;
			low >>= 8;
		}
		return commandId;
	}

	/// <summary>
	/// Deterministically derives the id of the <paramref name="ordinal"/>-th event a command expands to,
	/// carrying the <i>event's</i> semantic allocation rather than the command's.
	/// <para>
	/// The byte-level derivation legitimately differs by UUID version. When the command id is opaque
	/// (production: a v7 client id) there is nowhere to put a semantic class, so this is exactly
	/// <see cref="Derive(Guid, int)"/> — production event ids are bit-for-bit unchanged. When the command
	/// id is a <b>structured</b> <c>C0DE</c> v8 id the result is composed from three sources (model.md §8):
	/// the command instance supplies the company/scope prefix and the base node, the event <i>type</i>
	/// supplies the registry bit and the semantic class <c>Enn</c>, and this derivation supplies the
	/// <see cref="AllocationMode.Generated"/> bit. Only the 48-bit node advances by
	/// <paramref name="ordinal"/>, so a carry can never reach the mode or class.
	/// </para>
	/// <para>
	/// A structured command whose event type is <i>not</i> structured throws: emitting the command's own
	/// <c>Cnn</c> as if it were the event's class would be a false semantic claim.
	/// </para>
	/// </summary>
	public static Guid DeriveEventId(Guid commandId, Guid eventTypeId, int ordinal)
	{
		if (ordinal < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(ordinal));
		}
		if (!commandId.IsStructuredId())
		{
			return Derive(commandId, ordinal);
		}
		if (!eventTypeId.IsStructuredId())
		{
			throw new ArgumentException(
				$"A structured command id ({commandId}) cannot derive a structured event id from an unstructured event type id ({eventTypeId}): the result would carry the command's own semantic class."
				, nameof(eventTypeId)
			);
		}
		// registry bit from the event type, generated bit from this derivation; the command instance's
		// own mode is deliberately not consulted (model.md §8: generated projection preserves the
		// semantic allocation of the type, not of the thing it was derived from).
		var mode = AllocationMode.Variant | AllocationMode.Generated | (eventTypeId.GetAllocationMode() & AllocationMode.Staging);
		return commandId
			.WithAllocation(mode, eventTypeId.GetSemanticClass())
			.AdvanceNode((ulong)ordinal)
			;
	}
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
	[Obsolete("Collections are retired — see ISynqraIdProvider.CreateCollectionId.")]
	public Guid CreateCollectionId() => GuidExtensions.CreateVersion7();
	public Guid CreateLinkId() => GuidExtensions.CreateVersion7();
	[Obsolete("Call the static SynqraIdDerivation.DeriveEventId instead — see ISynqraIdProvider.CreateEventId.")]
	public Guid CreateEventId(Guid commandId, Guid eventTypeId, int ordinal) => SynqraIdDerivation.DeriveEventId(commandId, eventTypeId, ordinal);
}

/// <summary>
/// Test <see cref="ISynqraIdProvider"/> — predictable, per-class monotonic <b>generated</b> ids
/// (<c>C0DE0000-0000-8000-{mode}{class}-{counter}</c>, see docs/model.md §8) so ids minted by production
/// code under test read cleanly in logs and assertions. The mode keeps the semantic registry of the type
/// being instantiated and only sets <see cref="AllocationMode.Generated"/>: a committed type yields an
/// <c>A</c> instance, a staging type a <c>B</c> one. Registered by the test host in place of
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

	public Guid CreateCommandId(Guid commandTypeId) => New(ModeOf(commandTypeId), ClassOf(commandTypeId, family: 0xC), ref _command, 0x100);
	public Guid CreateStreamId() => New(AllocationMode.CommittedGenerated, 0x005, ref _stream, 1);
	public Guid CreateComponentId() => New(AllocationMode.CommittedGenerated, 0x001, ref _component, 1);
	[Obsolete("Collections are retired — see ISynqraIdProvider.CreateCollectionId.")]
	public Guid CreateCollectionId() => New(AllocationMode.CommittedGenerated, 0x002, ref _collection, 1);
	public Guid CreateLinkId() => New(AllocationMode.CommittedGenerated, 0x003, ref _link, 1);
	[Obsolete("Call the static SynqraIdDerivation.DeriveEventId instead — see ISynqraIdProvider.CreateEventId.")]
	public Guid CreateEventId(Guid commandId, Guid eventTypeId, int ordinal) => SynqraIdDerivation.DeriveEventId(commandId, eventTypeId, ordinal);

	/// <summary>
	/// The mode a generated instance of <paramref name="typeId"/> must carry: the type's own semantic
	/// registry, plus <see cref="AllocationMode.Generated"/>. A type with no structured id has no registry
	/// of its own, so its synthesised class is treated as a committed allocation.
	/// </summary>
	static AllocationMode ModeOf(Guid typeId) =>
		AllocationMode.Variant
		| AllocationMode.Generated
		| (typeId.IsStructuredId() ? typeId.GetAllocationMode() & AllocationMode.Staging : 0)
		;

	/// <summary>
	/// The semantic class an instance of <paramref name="typeId"/> should carry. A structured type id
	/// already owns a family + code, so it is taken verbatim. A type with no structured id (a consumer type
	/// whose <c>[SynqraModel]</c> is parameterless, so its id is a v5 hash) has no allocated code; one is
	/// synthesised from the hash into the high half <c>0x80..0xFF</c> — stable across runs, never
	/// <c>00</c> (which would read as the family's base type), and visibly "unallocated" at a glance.
	/// </summary>
	static unsafe ushort ClassOf(Guid typeId, int family)
	{
		if (typeId.IsStructuredId())
		{
			return typeId.GetSemanticClass();
		}
		byte* b = (byte*)&typeId;
		return (ushort)((family << 8) | (b[15] | 0x80));
	}

	static Guid New(AllocationMode mode, ushort @class, ref long counter, long step) =>
		new Guid($"C0DE0000-0000-8000-{(byte)mode:X1}{@class:X3}-{Interlocked.Add(ref counter, step):X12}");
}
