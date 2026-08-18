namespace Synqra;

/// <summary>
/// Reads and composes <b>CODE v8</b> ids — the <c>C0DE</c>-prefixed structured UUIDv8 convention
/// described in docs/model.md §8, where the RFC 9562 variant nibble carries an
/// <see cref="AllocationMode"/> and the two bytes after it carry a 12-bit semantic class.
/// <para>
/// This is deliberately <b>not</b> part of <see cref="GuidExtensions"/>. That type implements only what
/// RFC 9562 actually specifies (versions 1/3/4/5/6/7/8-hash, variant and timestamp accessors) and is
/// meant to ship on its own as a general-purpose UUID package. CODE v8 is an invented, application-level
/// layout <i>on top of</i> the RFC: it assigns meaning to bits the RFC leaves free. Mixing the two would
/// put a private convention into a package whose whole value is being standards-only, so the invented
/// layer lives here instead.
/// </para>
/// </summary>
public static class CodeGuidExtensions
{
	/// <summary>
	/// True when <paramref name="guid"/> is a <b>structured</b> CODE id: the <c>C0DE</c> magic prefix
	/// plus RFC 9562 version 8, i.e. an application-defined layout whose group-4 carries an allocation
	/// mode nibble
	/// and a 12-bit semantic class (model.md §8). Opaque ids — v7 data, v5 derived type ids, v4 tokens,
	/// and unrelated v8 hashes such as <see cref="GuidExtensions.CreateVersion8_Sha256(Guid, string)"/> —
	/// are not structured, so callers must not read a class out of them.
	/// </summary>
	public static unsafe bool IsStructuredId(this Guid guid)
	{
		if (guid == Guid.Empty)
		{
			return false;
		}
		if (guid.GetVariant() != 1 || guid.GetVersion() != 8)
		{
			return false;
		}
		byte* b = (byte*)&guid;
		// group 1 is stored in native order, so the C0DE magic lands in bytes 3,2 on little-endian
		return BitConverter.IsLittleEndian
			? b[3] == 0xC0 && b[2] == 0xDE
			: b[0] == 0xC0 && b[1] == 0xDE
			;
	}

	/// <summary>
	/// The allocation mode of a structured id — the RFC 9562 variant nibble, whose two free low bits are
	/// <b>orthogonal</b>: <see cref="AllocationMode.Staging"/> selects the semantic registry and
	/// <see cref="AllocationMode.Generated"/> the provenance. It is never a flat four-value enum; see
	/// <see cref="AllocationMode"/> and model.md §8.
	/// </summary>
	public static unsafe AllocationMode GetAllocationMode(this Guid guid)
	{
		byte* b = (byte*)&guid;
		return (AllocationMode)(b[8] >> 4);
	}

	/// <summary>True when the id belongs to the committed (normative, firm) semantic registry.</summary>
	public static bool IsCommitted(this Guid guid) => (guid.GetAllocationMode() & AllocationMode.Staging) == 0;

	/// <summary>True when the id belongs to the staging (working, still-mutable) semantic registry.</summary>
	public static bool IsStaging(this Guid guid) => (guid.GetAllocationMode() & AllocationMode.Staging) != 0;

	/// <summary>True when the id was minted by a generator rather than hand-allocated in a registry.</summary>
	public static bool IsGenerated(this Guid guid) => (guid.GetAllocationMode() & AllocationMode.Generated) != 0;

	/// <summary>True when the id was pinned by hand in a registry rather than generated.</summary>
	public static bool IsPinned(this Guid guid) => (guid.GetAllocationMode() & AllocationMode.Generated) == 0;

	/// <summary>
	/// The 12-bit semantic class <c>Fnn</c> of a structured id: the semantic family <c>F</c> (high 4 bits)
	/// plus the code <c>nn</c> allocated inside that family <i>and</i> registry (low 8 bits). For a type id
	/// this is the type's own class; for an instance id it is the class of the type it instantiates.
	/// <para>
	/// The class alone does not identify a type — semantic identity is <c>registry + Fnn</c>, because the
	/// committed and staging registries allocate codes independently.
	/// </para>
	/// </summary>
	public static unsafe ushort GetSemanticClass(this Guid guid)
	{
		byte* b = (byte*)&guid;
		return (ushort)(((b[8] & 0x0F) << 8) | b[9]);
	}

	/// <summary>The semantic family nibble <c>F</c> of <see cref="GetSemanticClass"/>.</summary>
	public static unsafe byte GetSemanticFamily(this Guid guid)
	{
		byte* b = (byte*)&guid;
		return (byte)(b[8] & 0x0F);
	}

	/// <summary>The family-local semantic code <c>nn</c> of <see cref="GetSemanticClass"/>.</summary>
	public static unsafe byte GetSemanticCode(this Guid guid)
	{
		byte* b = (byte*)&guid;
		return b[9];
	}

	/// <summary>
	/// Rewrites group-4 — the allocation mode nibble plus the 12-bit semantic class — leaving the
	/// company/scope prefix and the 48-bit node untouched. The inverse of
	/// <see cref="GetAllocationMode"/> + <see cref="GetSemanticClass"/>.
	/// </summary>
	public static unsafe Guid WithAllocation(this Guid guid, AllocationMode mode, ushort semanticClass)
	{
		byte* b = (byte*)&guid;
		b[8] = (byte)(((byte)mode << 4) | ((semanticClass >> 8) & 0x0F));
		b[9] = (byte)semanticClass;
		return guid;
	}

	/// <summary>
	/// Advances the 48-bit instance node (bytes 10..15) by <paramref name="delta"/>, leaving every
	/// other field — prefix, mode and semantic class — untouched, so a carry can never escape into
	/// them. Throws rather than wrap when the node space is exhausted.
	/// </summary>
	public static unsafe Guid AdvanceNode(this Guid guid, ulong delta)
	{
		byte* b = (byte*)&guid;
		ulong node = 0;
		for (int i = 10; i < 16; i++)
		{
			node = (node << 8) | b[i];
		}
		node += delta;
		if (node > 0xFFFFFFFFFFFFUL)
		{
			throw new ArgumentOutOfRangeException(nameof(delta), "Advanced node overflows the 48-bit instance space");
		}
		for (int i = 15; i >= 10; i--)
		{
			b[i] = (byte)node;
			node >>= 8;
		}
		return guid;
	}
}
