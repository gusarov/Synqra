namespace Synqra.BinarySerializer;

/// <summary>The frame being written ran past the end of its buffer; the writer may try again in a larger one.</summary>
public class SbxBufferTooSmallException : ArgumentException
{
	public SbxBufferTooSmallException(string what)
		: base($"Buffer too small for {what} encoding.")
	{
	}
}
