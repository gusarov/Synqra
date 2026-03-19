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
	 *   - 0x00C: Stream / Container (historical: C for Container, the first class allocated in Synqra)
	 *
	 * Reserved UUIDs:
	 *   - C0DE0000-0000-8000-8000-000000000000 is a reserved UUID to identify principles behind custom UUIDs (vendor-neutral)
	 *   - C0DEADD0-1032-8000-8000-000000000000 is a reserved synqra-zero UUID to identify "the Synqra UUID reservations table and principles document". Sha256('synqra')[..4] = ADD01032
	 *
	 * For Synqra: SHA256("synqra") → first 4 bytes → ADD01032
	 * To compute: pwsh -c "$h=[Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes('synqra'));($h[0..3]|%{$_.ToString('X2')})-join''"
	 */

	public static Guid SynqraTypeNamespaceId = new("BAD8F923-FA74-4CA0-9AA3-70BB874ACC76"); // NEVER CHANGE THAT! Object type namespace. It does not matter, what pattern it follows, it is just a random but fixed input to sha256 v8 guids it produces from type names.
	public static Guid SynqraRootStreamId    = new("C0DEADD0-1032-8000-800C-000000000000"); // class 0x00C: root/default stream. StreamId used to be ContainerId, C stands for Container. The first class allocated in Synqra.

	/*
	 * Principles behind MasterId:
	 * - The MasterId is a special identifier that represents the order of entries as was decided by master.
	 * - It is designed to be unique and immutable, ensuring that the identity and sequence it represents remains constant over time.
	 * - The MasterId is typically the same LocalId assigned at the creation of the entity and is used throughout its lifecycle, but if master rejects original LocalId due to monotonic violations, the MasterId will be granted by master at the time of acceptance.
	 * - It is important to avoid reusing or repurposing MasterIds to maintain the integrity of the identity they represent.
	 * 
	 * - Internally Master Id brings typical distributed clock information:
	 * - Term: 32 bits - allows for 4,294,967,296 terms, which is usually sufficient for most applications. Each term represents a period during which a particular master is active.
	 * - Sequence: 32 bits - allows for 4,294,967,296 sequences within each term. This provides a large range for ordering events or entries within the same term.
	 * - CollectionId: 64 bits - allows for random assignment of unique collection id and is a game changer in this structure, because MasterId becomes globally unique across all collections in the world.
	 * This means that entries from different collections can be compared and ordered without any risk of collision.
	 *
	 * - TTTTTTTT-SSSS-8SSS-827D-CCCCCCCCCCCC
	 * - T - Term
	 * - S - Sequence
	 * - 8   - variant bits (0b10) + nibble 0x2
	 * - 27D   - class 0x27D in the CCC scheme — distinguishes MasterIds from C0DE-prefixed custom UUIDs in v8 space.
	 *          Yes the C0DEyyyy-yyyy prefix is still the preferable approach, but MasterId needs all leading bits for Term/Sequence,
	 *          so the class bytes 0x27D is how they are recognized instead.
	 * - C - CollectionId
	 * 
	 * 
	 * - 00000000-0000-8000-827d-xxxxxxxxxxxx is a MasterID that points at zero event. It is reserved and should never be used as a real MasterId. Instead, it is used to identify the Collection, as CollectionId. Same bytes are set in every collection.
	 * - 00000000-0000-8000
	 */

}
