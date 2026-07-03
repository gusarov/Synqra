namespace Synqra;

/// <summary>
/// Ambient stream_id a caller must establish before touching a stream-scoped store — deliberately
/// <see cref="AsyncLocal{T}"/>-based rather than a constructor/method parameter, because a store
/// like <c>MongoProjection</c> is a process-wide singleton (so one server process can serve however
/// many concurrent streams it has, without a per-stream instance to construct or cache), and the
/// stream a given call is authorized for is a property of <i>that call</i>, not of the store. Never
/// exposed as a parameter on <see cref="IObjectStore"/>/<see cref="IProjection"/>/<see cref="ISynqraCollection"/>
/// — every stream-scoped query reads this instead, so there is exactly one place a caller can get it
/// wrong (forgetting to enter a scope), not one wrong parameter per call site.
/// <para>
/// There is deliberately no process-wide default. A stream_id is a security boundary, not a
/// convenience setting — a real server has no such thing as "the" stream, and a host that forgets
/// to enter a scope must fail loudly (<see cref="Current"/> throws) rather than silently serve
/// whatever a fallback constant happened to point at. A test host establishes a scope the same way
/// a production request handler does — see the matrix tests' own <c>[Before(Test)]</c> hook — not
/// through a special-cased default that only exists for tests.
/// </para>
/// <para>
/// The same mechanism is meant to carry a future snapshot/projection-key selector alongside the
/// stream_id — both are "which slice of the world is this call authorized to see," decided once per
/// call and threaded ambiently, not re-derived at every query site.
/// </para>
/// </summary>
public static class SynqraStreamContext
{
	static readonly AsyncLocal<Guid?> _current = new();

	/// <summary>The stream_id in effect for the calling async flow. Throws if no <see cref="Enter"/>
	/// scope is active — there is no fallback.</summary>
	public static Guid Current => _current.Value
		?? throw new InvalidOperationException("No stream context is active. Establish one with SynqraStreamContext.Enter(streamId) before touching a stream-scoped store.");

	/// <summary>The stream_id in effect for the calling async flow, or <c>null</c> if no <see cref="Enter"/>
	/// scope is active. Unlike <see cref="Current"/> this never throws — it is the non-throwing peek a
	/// store uses to <i>compare</i> the ambient scope against a stream it is pinned to.</summary>
	public static Guid? CurrentOrNull => _current.Value;

	/// <summary>
	/// Scopes <see cref="Current"/> to <paramref name="streamId"/> for the calling async flow (and
	/// anything it awaits) until the returned scope is disposed. Nests correctly — disposing restores
	/// whatever was in effect before this call (typically nothing, since callers are expected to
	/// scope per request rather than nest broad-then-narrow).
	/// </summary>
	public static IDisposable Enter(Guid streamId)
	{
		if (streamId == default)
		{
			throw new ArgumentException("streamId must not be default.", nameof(streamId));
		}
		var previous = _current.Value;
		_current.Value = streamId;
		return new Scope(previous);
	}

	/// <summary>
	/// Resolve the stream id a store call is scoped to, for a store that can register <b>either</b> as
	/// the process-wide multitenant root (no pinned stream — it reads the caller's ambient scope on
	/// every access, so one instance serves however many concurrent streams the process has) <b>or</b>
	/// pinned to a single stream. The policy is identical across every such store (currently the MongoDb
	/// and File projections), so it lives here once rather than being copy-pasted into each — the ambient
	/// scope is only the carrier; deciding what to do with it is this one method.
	/// <para>
	/// <paramref name="pinnedStreamId"/> is <c>null</c> => the store is the multitenant root: return the
	/// ambient <see cref="Current"/> (which throws if no scope is active — there is no default stream, a
	/// stream id is a security boundary).
	/// </para>
	/// <para>
	/// A value => the store is pinned to that one stream: return it, but an ambient scope for a
	/// <i>different</i> stream is a caller bug (entered stream B, then touched a store bound to stream A)
	/// and throws. A matching scope, or no scope at all, passes — so a pinned store stays usable both
	/// outside any scope and inside a correctly-matching one.
	/// </para>
	/// </summary>
	public static Guid Resolve(Guid? pinnedStreamId)
	{
		if (pinnedStreamId is Guid pinned)
		{
			if (CurrentOrNull is Guid ambient && ambient != pinned)
			{
				throw new InvalidOperationException(
					$"Stream-context conflict: this store is pinned to stream {pinned}, but an ambient "
					+ $"SynqraStreamContext scope for a different stream {ambient} is active. A store pinned "
					+ "to one stream must only be touched outside any scope, or inside a scope for its own stream.");
			}
			return pinned;
		}
		return Current;
	}

	sealed class Scope(Guid? previous) : IDisposable
	{
		public void Dispose() => _current.Value = previous;
	}
}
