
namespace XKit.LoggingCommands;

public class StreamLoggingCommandProcessor : System.IO.Stream
{
	private readonly Stream _inputStream;

	public StreamLoggingCommandProcessor(Stream inputStream)
	{
		_inputStream = inputStream;
	}

	public override bool CanRead => _inputStream.CanRead;

	public override bool CanSeek => _inputStream.CanSeek;

	public override bool CanWrite => _inputStream.CanWrite;

	public override long Length => _inputStream.Length;

	public override long Position { get => _inputStream.Position; set => _inputStream.Position = value; }

	public override void Flush()
	{
		_inputStream.Flush();
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return _inputStream.Read(buffer, offset, count);
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		return _inputStream.Seek(offset, origin);
	}

	public override void SetLength(long value)
	{
		_inputStream.SetLength(value);
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		_inputStream.Write(buffer, offset, count);
	}
}
