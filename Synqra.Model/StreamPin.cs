namespace Synqra;

/// <summary>
/// Optional construction-time pin for an omnitenant store (File / Mongo). It is deliberately
/// <b>never registered in DI</b>: the omnitenant root registration therefore receives <c>null</c> for
/// its optional <see cref="StreamPin"/> constructor dependency and resolves the caller's ambient
/// <see cref="SynqraStreamContext"/> scope on every access. A factory that builds a genuinely
/// single-tenant instance at the point of use passes one of these to bind the store to exactly one
/// stream — pinning happens at construction by a factory, never by a stream-parameterised DI
/// registration (that would reintroduce the "singleton bound to a fixed stream" anti-pattern).
/// <para>
/// There is no "default stream": a pin always carries a real, non-default stream id (a stream id is a
/// security boundary).
/// </para>
/// </summary>
public sealed class StreamPin
{
	public StreamPin(Guid streamId)
	{
		if (streamId == default)
		{
			throw new ArgumentException(
				"A pinned stream id must not be default — there is no default stream (a stream id is a security boundary).",
				nameof(streamId));
		}
		StreamId = streamId;
	}

	public Guid StreamId { get; }
}
