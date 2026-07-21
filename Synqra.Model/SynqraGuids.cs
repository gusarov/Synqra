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
	 * - A "class" field (CCC) categorizes UUID types within the project: C0DEyyyy-yyyy-8xxx-8CCC-xxxxxxxxxxxx
	 *   - The class is 12 bits (1.5 bytes, 3 hex chars), allowing up to 4096 classes per project
	 *   - Byte 8: variant (0b10) + 2 fixed zero bits + top 4 bits of class → always starts with hex 8
	 *   - Byte 9: bottom 8 bits of class
	 * - The remaining xxx and xxxxxxxxxxxx bits are available for sub-versioning, counters, or identifiers
	 * - The sub-version 0 (xxx=000) is for constant guids. It should be guaranteed that there will be no guids of that version with dynamic parts.
	 * - The other sub-versions are reserved.
	 * - The remaining bits are available for customization or counters.
	 *
	 * Well-known classes:
	 *   - 0x000: Object type namespace (generic object; no type-specific knowledge from the ID)
	 *   - 0x00C: Stream (historical: the hex digit "C" comes from this class's original name,
	 *     ContainerId — kept for the byte value only; "container" itself is retired vocabulary now
	 *     that Components legitimately use that word for something unrelated, IComponentContainer.
	 *     Stream and node are the two concepts that exist; the first class allocated in Synqra)
	 *
	 * Reserved UUIDs:
	 *   - C0DE0000-0000-8000-8000-000000000000 is a reserved UUID to identify principles behind custom UUIDs (vendor-neutral)
	 *   - C0DEADD0-1032-8000-8000-000000000000 is a reserved synqra-zero UUID to identify "the Synqra UUID reservations table and principles document". Sha256('synqra')[..4] = ADD01032
	 *   - C0DEADD0-1032-8000-8000-000000000001 is the Synqra object-type namespace (class 000, node 1) — the fixed salt for derived (v5) type ids. See SynqraTypeNamespaceId.
	 *   - C0DEADD0-1032-8000-800C-000000000000 is a reserved synqra GUID for root/default stream id (before stream id is fully supported in a system, it is a reserved field and requires reserved value to avoid zeros validation)
	 *
	 * For Synqra: SHA256("synqra") → first 4 bytes → ADD01032
	 * To compute: pwsh -c "$h=[Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes('synqra'));($h[0..3]|%{$_.ToString('X2')})-join''"
	 */

	// The Synqra object-type namespace: the fixed salt fed to CreateVersion5(namespace, type.FullName)
	// to derive a type id for any [SynqraModel] type that does not carry an explicit id
	// (see TypeMetadataProvider). Follows the v8 C0DE convention — C0DE + sha256("synqra")[..4]=ADD01032
	// + version 8 + class 000 (object-type namespace) + node 1 (…0000 is the reserved synqra-zero doc).
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
