using Synqra.AppendStorage;

namespace Synqra.Projection.Sqlite;

internal class AttachedObjectData
{
	public required Guid Id { get; init; }
	public required ISynqraCollection Collection { get; init; }
	public required bool IsJustCreated { get; set; }
}
