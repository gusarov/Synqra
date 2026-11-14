namespace Synqra.BinarySerializer;

/// <summary>
/// A frame names a type id this build does not map, as a newer peer's frame does. SBX frames are not length-prefixed per value, so
/// the rest of the frame cannot be read past it: the whole frame is unreadable here, and reading on misreads it.
/// </summary>
public class SbxUnknownTypeException : InvalidOperationException
{
	public int TypeId { get; }

	public SbxUnknownTypeException(int typeId)
		: base($"SBX type id {typeId} is not mapped by this build.")
	{
		TypeId = typeId;
	}
}
