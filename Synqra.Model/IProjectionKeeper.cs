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
    /// Position of the last event applied to this projection: its <see cref="Event.SequenceId"/> in the stream's log, which
    /// is arrival order on a client and commit order on a server (docs/model.md §9). <see cref="Guid.Empty"/> means cold.
    /// </summary>
    Guid Cursor { get; }

    /// <summary>
    /// Apply one event that arrived from elsewhere (a socket), advancing <see cref="Cursor"/> to its stamp when it has one.
    /// <paramref name="isReplay"/> suppresses one-shot activator side effects for historical events.
    /// </summary>
    Task ApplyAsync(Event ev, bool isReplay = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fold in this stream's events after <see cref="Cursor"/>, read by <paramref name="readAfter"/> (the log's events of this
    /// stream strictly after a cursor), as one write of this projection: a command submitted meanwhile waits for it, so an
    /// event the projection applied itself is never applied again from the log. The <see cref="IProjectionKeeper"/> calls this.
    /// </summary>
    Task CatchUpAsync(Func<Guid, IAsyncEnumerable<Event>> readAfter, bool isReplay = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// The one and only replay path. Brings a projection up to date by reading the delta from its
/// <see cref="IReplayProjection.Cursor"/> forward and applying it. Cheap no-op when already at head,
/// full replay when cold. Consumers call this before use — they never call a "Load" method.
/// </summary>
public interface IProjectionKeeper
{
    Task MaintainAsync(IReplayProjection projection, bool isReplay = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// The projection-area factory: creates a fresh, single-stream <see cref="IReplayProjection"/> bound
/// to the supplied stream id. The stream id is a runtime argument supplied at the call site (a fresh
/// random stream in tests, the session stream on a client) — never pinned into a DI registration.
/// <para>
/// A store whose projection
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
