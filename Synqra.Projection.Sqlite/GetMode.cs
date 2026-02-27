using Synqra.AppendStorage;

namespace Synqra.Projection.Sqlite;

// It is not flags, as all possible permutations are defined explicitly
internal enum GetMode : byte
{
	// 0b_0000_0000
	//          MME
	// E - Behavior for existing object (0 - throw, 1 - return)
	// MM - Behavior for missing object (0 - throw, 1 - zero_default, 2 - create_id)

	// 0b_MM_E
	Invalid,     // 00 0
	RequiredId,  // 00 1
	MustAbsent,  // 01 0
	TryGet,      // 01 1
	RequiredNew, // 10 0
	GetOrCreate, // 10 1
}
