using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// A projection that materializes exactly one stream's state by folding in that stream's events.
/// It carries its own <see cref="StreamId"/> and a <see cref="Cursor"/> (the last applied event id),
/// so catch-up is always an explicit, incremental delta from the cursor — never an ambient
/// "first touch" and never a stream baked into a DI registration.
/// <para>
/// Extends <see cref="IObjectStore"/> so the projection produced by <see cref="IProjectionFactory"/>/
/// <see cref="IProjectionProvider"/> is directly usable as a queryable store. This is the
/// non-multitenant projection family (in-memory today, an instance-per-stream client store like
/// IndexedDb tomorrow); the multitenant durable projections implement <see cref="IObjectStore"/> +
/// <see cref="IProjection"/> directly and are resolved ambiently, never via a factory.
/// </para>
/// </summary>
public interface IReplayProjection : IObjectStore, IProjection
{
    /// <summary>The single stream this projection is bound to (Todo's ContainerId).</summary>
    Guid StreamId { get; }

    /// <summary>
    /// Id of the last event applied to this projection (Todo's CurrentVersion). <see cref="Guid.Empty"/>
    /// means cold — nothing applied yet.
    /// </summary>
    Guid Cursor { get; }

    /// <summary>
    /// Apply one event to this projection, advancing <see cref="Cursor"/> to the event's id. The
    /// <see cref="IProjectionKeeper"/> is the only caller during catch-up. <paramref name="isReplay"/>
    /// suppresses one-shot activator side effects for historical events.
    /// </summary>
    Task ApplyAsync(Event ev, bool isReplay = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// A per-stream event reader/writer. The log knows which stream it is (<see cref="StreamId"/>), so a
/// misrouted append is caught here rather than silently folded into the wrong projection.
/// </summary>
public interface IEventLog
{
    /// <summary>The stream this log is scoped to.</summary>
    Guid StreamId { get; }

    /// <summary>Append one event to this stream's durable log.</summary>
    Task AppendAsync(Event ev, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read this stream's events strictly AFTER <paramref name="afterEventId"/> (exclusive), optionally
    /// stopping once the event with id <paramref name="till"/> has been yielded. Passing
    /// <see cref="Guid.Empty"/> reads from the beginning.
    /// </summary>
    IAsyncEnumerable<Event> ReadFrom(Guid afterEventId = default, Guid? till = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hands out per-stream <see cref="IEventLog"/>s. The result is guaranteed to have
/// <c>GetEventLog(s).StreamId == s</c>.
/// </summary>
public interface IEventLogProvider
{
    IEventLog GetEventLog(Guid streamId);
}

/// <summary>
/// The one and only replay path. Brings a projection up to date by reading the delta from its
/// <see cref="IReplayProjection.Cursor"/> forward and applying it. Cheap no-op when already at head,
/// full replay when cold. Consumers call this before use — they never call a "Load" method.
/// </summary>
public interface IProjectionKeeper
{
    Task MaintainAsync(IReplayProjection projection, Guid? till = null, bool isReplay = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// The projection-area factory: creates a fresh, single-stream <see cref="IReplayProjection"/> bound
/// to the supplied stream id. The stream id is a runtime argument supplied at the call site (a fresh
/// random stream in tests, the session stream on a client) — never pinned into a DI registration.
/// <para>
/// This is the counterpart to the event-area <see cref="IEventLogProvider"/>. A store whose projection
/// cannot be multitenant (in-memory today, an instance-per-stream client store like IndexedDb
/// tomorrow) registers a factory here instead of a DI-resolvable projection singleton. Use this
/// directly only for an <i>alternative</i> build of a stream; for the regular latest projection of a
/// stream prefer <see cref="IProjectionProvider"/>.
/// </para>
/// </summary>
public interface IProjectionFactory
{
    IReplayProjection Create(Guid streamId);
}

/// <summary>
/// Hands out the regular, latest projection for a given stream id: one cached, keeper-maintained
/// <see cref="IReplayProjection"/> per stream, brought up to head before it is returned. This is what
/// consumers normally want — the "default provider/factory/DI-key gives me the latest projection of a
/// specified stream" entry point. Keyed by stream <b>at call time</b>, so it is NOT a stream-bound DI
/// singleton (mirrors Todo's <c>EventsStreamFactory.Get</c> GetOrAdd caching).
/// </summary>
public interface IProjectionProvider
{
    Task<IReplayProjection> GetAsync(Guid streamId, CancellationToken cancellationToken = default);
}
