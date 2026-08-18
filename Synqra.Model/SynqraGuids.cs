using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Synqra;

public static class SynqraGuids
{
	/*
	 * Principles behind custom UUIDs:
	 * - The large quantity of 0 bits is what makes custom UUIDs easy to recognize
	 * - It still should be 100% legit to modern spec, and 2024 RFC 9562 v8 made ideal space for that
	 * - To make any custom UUID instantly recognizable, let's use a hex-readable magic word as a prefix: C0DE
	 * - The format starts with C0DE to signal "this is a custom/code UUID": C0DE____-____-8___-8___-____________
	 * - To avoid collisions across projects without a central registry, a deterministic project hash follows the prefix
	 * - The hash is the first 4 bytes of SHA-256 of the lowercase UTF-8 project name: C0DEyyyy-yyyy-8___-8___-____________
	 * - Anyone can follow same principles by computing SHA256("<projectname>") for their own project
	 * - The previous approach used a random signature (2A21B27D). The hash approach is better because it is reproducible and verifiable.
	 * - The 8 nibbles at positions 13 and 17 satisfy UUIDv8 version (0b1000) and variant (0b10xx) per RFC 9562
	 * - Group 4 is an allocation-mode nibble plus a 12-bit semantic class: C0DEyyyy-yyyy-8xxx-mFnn-xxxxxxxxxxxx
	 *   - Byte 8 high nibble (m): RFC 9562 variant bits 0b10 + 2 FREE bits. Those two bits are two
	 *     ORTHOGONAL dimensions, not a flat four-value enum (see AllocationMode):
	 *
	 *                    pinned   generated
	 *       committed       8         A
	 *       staging         9         B
	 *
	 *     The staging bit selects the semantic REGISTRY; the generated bit records PROVENANCE only.
	 *     So 8C02 <-> AC02 and 9C17 <-> BC17 are the same semantic allocation seen as a pinned type
	 *     and as a generated instance, while 8C02 and 9C02 are independent allocations in two
	 *     independent registries. Promotion 9 -> 8 is a fresh allocation and may change the code.
	 *   - The semantic class is 12 bits (byte 8 low nibble + byte 9): a FAMILY nibble (F) plus a
	 *     family-local code (nn), allowing 4096 classes per registry.
	 *   - Read group 4 as m/F/nn: 8005 is mode 8, family 0, code 05 — NOT "family 5".
	 * - The trailing 48 bits (xxxxxxxxxxxx) are the node: a per-class instance counter
	 *
	 * Group 4 is used two ways, told apart by the node:
	 *   - Node all-zero → the value is a TYPE id. Families: C = commands, E = events,
	 *     A = envelopes/messages, 0 = default/unqualified (plain domain models and standalone
	 *     well-known values). Within a family, nn = 00 is that family's abstract base (8C00 Command,
	 *     8E00 Event), 0E/0F are other shared bases, and 01+ are concrete types.
	 *     See "Reserved built-in type ids" in docs/model.md.
	 *   - Node non-zero → the value is a well-known INSTANCE, and the class names what it is (below).
	 *     The class inside an instance id is a readability hint only: type resolution always goes
	 *     through an explicit type-id field, never through an instance id.
	 *
	 * Well-known codes in family 0:
	 *   - 0x000: no semantic type — one-off reserved values (e.g. SynqraTypeNamespaceId, node 1)
	 *   - 0x001: Component/entity instances
	 *   - 0x005: Stream — the mandatory security boundary; node is an instance counter
	 *   - 0x00C: NEVER ALLOCATE. This is family 0 code 0C, not "a command". A generic "some command"
	 *     instance class briefly lived here and must not return: commands are family C, and a command
	 *     instance carries the Cnn of the concrete command type it is. Command ids are spaced by
	 *     0x100 (…000100, …000200) so the low node byte holds their derived events.
	 *   - Retired: 0x002 (collection) and 0x003 (link). Do not re-allocate — existing test
	 *     fixtures still use them.
	 *   - Historical note: 0x00C originally meant Stream (the hex "C" came from its first name,
	 *     ContainerId). Stream moved to 0x005 when 0x00C was reassigned to Command; "container"
	 *     is retired vocabulary now that Components use that word for IComponentContainer.
	 *
	 * Family E is never allocated independently. An event instance id is always
	 * SynqraIdDerivation.DeriveEventId(commandId, eventTypeId, ordinal): it inherits the command's
	 * company/scope prefix and node lineage, takes its registry bit and family-local code from the
	 * EVENT TYPE, and sets the generated bit — so a committed event type 8E03 derives AE03 and a
	 * staging one 9E15 derives BE15. Family 0 is also the home of retired family F, which used to be
	 * the generic-model bucket and must not be re-introduced.
	 *
	 * Reserved UUIDs:
	 *   - C0DE0000-0000-8000-8000-000000000000 is a reserved UUID to identify principles behind custom UUIDs (vendor-neutral)
	 *   - C0DEADD0-1032-8000-8000-000000000000 is a reserved synqra-zero UUID to identify "the Synqra UUID reservations table and principles document". Sha256('synqra')[..4] = ADD01032
	 *   - C0DEADD0-1032-8000-8000-000000000001 is the Synqra object-type namespace (family 0 code 00, node 1) — the fixed salt for derived (v5) type ids. See SynqraTypeNamespaceId.
	 *
	 * For Synqra: SHA256("synqra") → first 4 bytes → ADD01032
	 * To compute: pwsh -c "$h=[Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes('synqra'));($h[0..3]|%{$_.ToString('X2')})-join''"
	 */

	// The Synqra object-type namespace: the fixed salt fed to CreateVersion5(namespace, type.FullName)
	// to derive a type id for any [SynqraModel] type that does not carry an explicit id
	// (see TypeMetadataProvider). Follows the v8 C0DE convention — C0DE + sha256("synqra")[..4]=ADD01032
	// + version 8 + family 0 code 00 (no semantic type: a standalone well-known value) + node 1
	// (…0000 is the reserved synqra-zero doc).
	// This value is a persisted contract: derived type ids are written into stored events, so once data
	// exists this MUST NOT change. (It was migrated once, from the legacy random BAD8F923… salt, to the
	// self-documenting C0DE form; affected types carry [SynqraLegacyTypeId] aliases for the old ids.)
	public static Guid SynqraTypeNamespaceId = new("C0DEADD0-1032-8000-8000-000000000001");

	// There is no default/root stream. A stream id is a first-class, mandatory value (a security
	// boundary): multitenant stores read the ambient SynqraStreamContext.Current (entered per
	// request); single-tenant stores take an explicit stream id at registration / borrow a
	// projection for an explicit stream from the provider.
	//
	// (Legacy, removed: the reserved root/default-stream UUID and the MasterId term/sequence
	// ordering scheme — Synqra has no master election or monotonic cluster clock. See docs/model.md.)
}
