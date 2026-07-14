using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Synqra.Projection.InMemory;

/// <summary>
/// The projection-area factory for the in-memory store. Creates a single-stream
/// <typeparamref name="TProjection"/> (an <see cref="InMemoryProjection"/> or a domain subclass such
/// as Contoso's) on demand. The stream id is a runtime argument supplied at the call site (a fresh
/// random stream in tests, the session stream on a client) — never pinned into a DI registration.
/// In-memory projections cannot be multitenant, so there is no DI-resolvable projection singleton:
/// callers use this factory (for an alternative build) or <see cref="IProjectionProvider"/> (for the
/// regular latest projection of a stream).
/// </summary>
public class InMemoryProjectionFactory<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProjection> : IProjectionFactory
	where TProjection : InMemoryProjection
{
	private readonly IServiceProvider _serviceProvider;

	public InMemoryProjectionFactory(IServiceProvider serviceProvider)
	{
		_serviceProvider = serviceProvider;
	}

	public IReplayProjection Create(Guid streamId)
	{
		if (streamId == default)
		{
			throw new ArgumentException(
				"A non-default stream id is required — there is no default stream (a stream id is a security boundary).",
				nameof(streamId));
		}
		// ActivatorUtilities.CreateInstance<T> carries [DynamicallyAccessedMembers(PublicConstructors)]
		// on T; the matching annotation on TProjection above flows it through, so the WASM trimmer
		// preserves the constructor and its optional parameters — streamId binds to the Guid ctor
		// param, the rest resolve from DI.
		return ActivatorUtilities.CreateInstance<TProjection>(_serviceProvider, streamId);
	}
}

/// <summary>Default in-memory projection factory producing the base <see cref="InMemoryProjection"/>.</summary>
public sealed class InMemoryProjectionFactory : InMemoryProjectionFactory<InMemoryProjection>
{
	public InMemoryProjectionFactory(IServiceProvider serviceProvider) : base(serviceProvider)
	{
	}
}

/// <summary>
/// The regular latest-projection provider for the in-memory store: one cached, keeper-maintained
/// <see cref="InMemoryProjection"/> per stream. Keyed by stream at call time (a
/// <see cref="ConcurrentDictionary{Guid, IReplayProjection}"/>), not a stream-bound DI singleton —
/// mirrors Todo's <c>EventsStreamFactory.Get</c> GetOrAdd caching. Each returned projection is brought
/// up to head via <see cref="IProjectionKeeper.MaintainAsync"/> before hand-out.
/// </summary>
public sealed class InMemoryProjectionProvider : IProjectionProvider
{
	private readonly IProjectionFactory _factory;
	private readonly IProjectionKeeper _keeper;
	private readonly ConcurrentDictionary<Guid, Entry> _byStream = new();

	public InMemoryProjectionProvider(IProjectionFactory factory, IProjectionKeeper keeper)
	{
		_factory = factory;
		_keeper = keeper;
	}

	// One entry per stream: the cached projection, a gate that serializes catch-up (GetAsync is
	// called on every "new events available" signal from the transport, possibly concurrently), and
	// a cold-load flag so only the very first catch-up is applied as a historical replay.
	private sealed class Entry
	{
		public IReplayProjection? Projection;
		public readonly SemaphoreSlim Gate = new(1, 1);
		public bool ColdLoaded;
	}

	public async Task<IReplayProjection> GetAsync(Guid streamId, CancellationToken cancellationToken = default)
	{
		if (streamId == default)
		{
			throw new ArgumentException("A non-default stream id is required.", nameof(streamId));
		}
		var entry = _byStream.GetOrAdd(streamId, _ => new Entry());
		await entry.Gate.WaitAsync(cancellationToken);
		try
		{
			entry.Projection ??= _factory.Create(streamId);
			// A freshly created projection is cold (Cursor == Guid.Empty): the whole durable log is
			// historical, so apply it as a replay (suppresses one-shot activator side effects). Once
			// caught up the first time, later live deltas are not replay.
			await _keeper.MaintainAsync(entry.Projection, isReplay: !entry.ColdLoaded, cancellationToken: cancellationToken);
			entry.ColdLoaded = true;
			return entry.Projection;
		}
		finally
		{
			entry.Gate.Release();
		}
	}

	public async Task<IReplayProjection> RebuildAsync(Guid streamId, CancellationToken cancellationToken = default)
	{
		if (streamId == default)
		{
			throw new ArgumentException("A non-default stream id is required.", nameof(streamId));
		}
		var entry = _byStream.GetOrAdd(streamId, _ => new Entry());
		await entry.Gate.WaitAsync(cancellationToken);
		try
		{
			var projection = _factory.Create(streamId);
			await _keeper.MaintainAsync(projection, isReplay: true, cancellationToken: cancellationToken);
			entry.Projection = projection;
			entry.ColdLoaded = true;
			return projection;
		}
		finally
		{
			entry.Gate.Release();
		}
	}
}

/// <summary>
/// An <see cref="IEventLog"/> over a multitenant <see cref="IAppendStorage{Event, Guid}"/>. The store
/// holds every stream (events are keyed by globally-unique v7 <see cref="Event.EventId"/> and carry
/// their own <see cref="Event.StreamId"/>), so reads are filtered to this log's stream — exactly
/// Todo's shared <c>RamEventsStore</c> filtered by ContainerId. The stream id also rejects a misrouted
/// append and satisfies the provider's <c>StreamId == streamId</c> guarantee.
/// </summary>
public sealed class AppendStorageEventLog : IEventLog
{
	private readonly IAppendStorage<Event, Guid> _storage;

	public AppendStorageEventLog(Guid streamId, IAppendStorage<Event, Guid> storage)
	{
		StreamId = streamId;
		_storage = storage;
	}

	public Guid StreamId { get; }

	public Task AppendAsync(Event ev, CancellationToken cancellationToken = default)
	{
		if (ev.StreamId != default && ev.StreamId != StreamId)
		{
			throw new InvalidOperationException(
				$"Event stream {ev.StreamId} does not match log stream {StreamId} — refusing misrouted append of {ev.EventId}.");
		}
		return _storage.AppendAsync(ev);
	}

	public async IAsyncEnumerable<Event> ReadFrom(
		  Guid afterEventId = default
		, Guid? till = null
		, [EnumeratorCancellation] CancellationToken cancellationToken = default
		)
	{
		// Backend-agnostic incremental read. IAppendStorage.GetAllAsync(from) is NOT a uniform
		// range scan across backends — the in-memory store treats `from` as a >= key bound, but the
		// JsonLines store treats it as a key *prefix* match (returning only the boundary event), and
		// others may differ. Relying on it for "everything after the cursor" silently returns nothing
		// on a prefix-match backend, breaking live catch-up. So always do a full read (from: default,
		// which every backend agrees means "everything") in append/chronological order and skip past
		// the cursor here. The log is multitenant, so also filter to this stream (a legacy event with
		// a default StreamId is tolerated — it belongs to whichever single stream owned the store
		// historically).
		//
		// A cold cursor (default) means "apply the whole stream". Otherwise skip every event up to and
		// including the one whose EventId == cursor (it is already applied), then yield the rest.
		var reached = afterEventId == default;
		await foreach (var ev in _storage.GetAllAsync(from: default, cancellationToken))
		{
			if (ev.StreamId != default && ev.StreamId != StreamId)
			{
				continue;
			}
			if (!reached)
			{
				if (ev.EventId == afterEventId)
				{
					reached = true;
				}
				continue;
			}
			yield return ev;
			if (till is Guid t && ev.EventId == t)
			{
				yield break;
			}
		}
	}
}

public sealed class EventLogProvider : IEventLogProvider
{
	private readonly IAppendStorage<Event, Guid> _storage;

	public EventLogProvider(IAppendStorage<Event, Guid> storage)
	{
		_storage = storage;
	}

	public IEventLog GetEventLog(Guid streamId)
	{
		if (streamId == default)
		{
			throw new ArgumentException("A non-default stream id is required.", nameof(streamId));
		}
		return new AppendStorageEventLog(streamId, _storage);
	}
}

/// <summary>
/// The one and only replay path: reads the delta from <see cref="IReplayProjection.Cursor"/> forward
/// out of the stream's <see cref="IEventLog"/> and applies it, advancing the cursor. A cheap no-op
/// when already at head, a full replay when cold.
/// </summary>
public sealed class ProjectionKeeper : IProjectionKeeper
{
	private readonly IEventLogProvider _logProvider;

	public ProjectionKeeper(IEventLogProvider logProvider)
	{
		_logProvider = logProvider;
	}

	public async Task MaintainAsync(
		  IReplayProjection projection
		, Guid? till = null
		, bool isReplay = false
		, CancellationToken cancellationToken = default
		)
	{
		var log = _logProvider.GetEventLog(projection.StreamId);
		await foreach (var ev in log.ReadFrom(afterEventId: projection.Cursor, till: till, cancellationToken))
		{
			await projection.ApplyAsync(ev, isReplay, cancellationToken);
		}
	}
}
