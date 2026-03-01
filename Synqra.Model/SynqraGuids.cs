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
	 * - It still should be 100% legit to modern spec, and 2024 rfc4122 v8 made ideal space for that
	 * - The bits are set to 0x________-____-8000-8000-____________ to satisfy previous statements.
	 * - To make sure any one can follow same principles, the custom_c should contain random signature
	 * - To still make random part readable, let's use some hex readable word in it, e.g. CODE
	 * - The bits are set to 0x________-____-8000-8000-C0DE2A21B27D where CODE is readable and 2A21B27D is just randomly generated for Synqra project
	 * - It makes total sense to have a version of the rest. So let's use 0x________-V___-8000-8000-C0DE2A21B27D as a next vendor specific version (4 bits, so 16 versions available, same as main guid version)
	 * - The sub-version 0 - is for constant guids. It should be guaranteed that there will be no guids of that version with dynamic parts.
	 * - The other sub-versions are reserved.
	 * - The remaining bits are available for customization or conters.
	 * - 00000000-0000-8000-8000-C0DE2A21B27D is a reserved synqra-zero UUID to identify this "the Synqra UUID reservations table and principles document"
	 * - 00000001-0000-8000-8000-C0DE2A21B27D is
	 */

	public static Guid SynqraTypeNamespaceId = new("BAD8F923-FA74-4CA0-9AA3-70BB874ACC76"); // This id is never visible and already in use
	public static Guid SynqraRootContainerId = new("00000000-000C-8000-8000-C0DE2A21B27D"); // This id is never visible and already in use
	// public static Guid SynqraTypeNamespaceId = new("00000001-0000-8000-8000-C0DE2A21B27D"); //

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
	 * - TTTTTTTT-SSSS-8SSS-827d-CCCCCCCCCCCC
	 * - T - Term
	 * - S - Sequence
	 * - 827d - fixed bytes to recognize MasterId and to respect other v8 custom ids. This allows to make all of them different in v8 space from other custom guids. Yes the -CODExxxxxxxx is still preferrable approach, but it is better to do at least this way than to spread across entire space.
	 * - C - CollectionId
	 * 
	 * 
	 * - 00000000-0000-8000-827d-xxxxxxxxxxxx is a MasterID that points at zero event. It is reserved and should never be used as a real MasterId. Instead, it is used to identify the Collection, as CollectionId. Same bytes are set in every colleciton.
	 * - 00000000-0000-8000
	 */

}
