using System.Buffers;

namespace Synqra.BinarySerializer;

/// <summary>
/// Writes a whole frame with a serializer that cannot size one ahead: the overflow is the measurement, and the frame
/// is written again into a buffer twice the size until it fits or passes the limit. The frame is one span either way;
/// a writer that streams into a growing sink is the follow-up this stands in for.
/// </summary>
public static class SbxFrameExtensions
{
	/// <summary>The most a frame may take before the writer gives up on it.</summary>
	public const int DefaultMaxFrameSize = 16 * 1024 * 1024;

	/// <summary>Whether the serializer threw because the frame ran past the end of its buffer: its own check, or the span indexer and slices.</summary>
	public static bool IsBufferOverflow(this Exception ex)
		=> ex is SbxBufferTooSmallException or IndexOutOfRangeException or ArgumentOutOfRangeException;

	/// <summary>
	/// One attempt into the caller's span: false when the frame does not fit. An overflow resets the serializer, so
	/// the next attempt starts clean: a partial frame already interned strings it never carried.
	/// </summary>
	public static bool TrySerializeFrame<T>(this ISbxSerializer serializer, in T value, Span<byte> destination, out int written)
	{
		bool fits;
		written = 0;
		try
		{
			serializer.Serialize(destination, ref written, value);
			fits = true;
		}
		catch (Exception ex) when (ex.IsBufferOverflow())
		{
			// not a failure: the caller retries in a larger buffer
			written = 0;
			serializer.Reset();
			fits = false;
		}
		return fits;
	}

	/// <summary>
	/// The frame, written into <paramref name="buffer"/> when it fits, else into a larger array of its own. With a
	/// <paramref name="pool"/>, every outgrown array (the first one included) goes back to it and the array the frame
	/// came back in is the caller's to return; without one, the frame's array is a plain allocation.
	/// </summary>
	public static ArraySegment<byte> SerializeFrame<T>(this ISbxSerializer serializer, in T value, ArraySegment<byte> buffer, ArrayPool<byte>? pool = null, int maxFrameSize = DefaultMaxFrameSize)
	{
		var current = buffer;
		ArraySegment<byte> frame = default;
		var fits = false;
		while (!fits)
		{
			var written = 0;
			try
			{
				serializer.Serialize(current, ref written, value);
				frame = current.Slice(0, written);
				fits = true;
			}
			catch (Exception ex) when (ex.IsBufferOverflow())
			{
				pool?.Return(current.Array!);
				if (current.Count >= maxFrameSize)
				{
					throw new SbxFrameTooLargeException(maxFrameSize, ex);
				}
				serializer.Reset(); // a partial frame already interned strings it never carried
				var larger = Math.Min(Math.Max(current.Count, 1) * 2, maxFrameSize);
				current = new ArraySegment<byte>(pool?.Rent(larger) ?? new byte[larger]);
			}
		}
		return frame;
	}
}
