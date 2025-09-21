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
	// public static Guid SynqraTypeNamespaceId = new("00000001-0000-8000-8000-C0DE2A21B27D"); //
}
