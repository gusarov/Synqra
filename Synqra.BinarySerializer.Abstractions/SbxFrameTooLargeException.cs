namespace Synqra.BinarySerializer;

/// <summary>A frame outgrew the most a writer allows; the cause is the overflow of the last attempt.</summary>
public class SbxFrameTooLargeException : InvalidOperationException
{
	public int Limit { get; }

	public SbxFrameTooLargeException(int limit, Exception inner)
		: base($"SBX frame does not fit in {limit} bytes, the most a frame may take.", inner)
	{
		Limit = limit;
	}
}
