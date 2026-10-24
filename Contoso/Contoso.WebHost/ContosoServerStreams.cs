using System.Collections.Concurrent;
using Contoso.Model;
using Synqra;

namespace Contoso.WebHost;

/// <summary>The streams this server created: a visitor asks for one, as a Quotaly user is given a main stream on sign-up.</summary>
public sealed class ContosoServerStreams(ISynqraIdProvider ids) : IContosoServerStreams
{
	readonly ConcurrentDictionary<Guid, byte> _streams = new();

	public Guid CreateStream()
	{
		var stream = ids.CreateStreamId();
		_streams.TryAdd(stream, 0);
		return stream;
	}

	public bool Contains(Guid streamId) => _streams.ContainsKey(streamId);
}
