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
	private readonly IServiceProvider? _services;
	private readonly ConcurrentDictionary<Guid, Entry> _byStream = new();

	public InMemoryProjectionProvider(IProjectionFactory factory, IProjectionKeeper keeper, IServiceProvider? services = null)
	{
		_factory = factory;
		_keeper = keeper;
		_services = services;
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
		EmergencyLog.Default.Stage($"provider {streamId}: waiting for the stream's gate (free {entry.Gate.CurrentCount == 1})");
		await entry.Gate.WaitAsync(cancellationToken);
		EmergencyLog.Default.Stage($"provider {streamId}: gate held");
		try
		{
			entry.Projection ??= _factory.Create(streamId);
			// A freshly created projection is cold (Cursor == Guid.Empty): the whole durable log is
			// historical, so apply it as a replay (suppresses one-shot activator side effects). Once
			// caught up the first time, later live deltas are not replay.
			try
			{
				await _keeper.MaintainAsync(entry.Projection, isReplay: !entry.ColdLoaded, cancellationToken: cancellationToken);
			}
			catch (UnreadableRecordException unreadable)
			{
				await RecoverAsync(entry, streamId, unreadable, cancellationToken);
			}
			entry.ColdLoaded = true;
			return entry.Projection;
		}
		finally
		{
			entry.Gate.Release();
			EmergencyLog.Default.Stage($"provider {streamId}: gate released");
		}
	}

	/// <summary>
	/// A local log the cold fold cannot read is dropped by its owner (the replication client), and a fresh projection folds what it
	/// kept; the server fills the rest. A projection already handed out keeps its identity: its log is dropped, and it reports so.
	/// </summary>
	async Task RecoverAsync(Entry entry, Guid streamId, UnreadableRecordException unreadable, CancellationToken cancellationToken)
	{
		var recovery = _services?.GetService<ILocalLogRecovery>() ?? _services?.GetService<IEventReplicationService>() as ILocalLogRecovery;
		if (recovery is null)
		{
			throw new InvalidOperationException($"The event log of stream {streamId} cannot be read and nothing here can drop and refetch it.", unreadable);
		}
		else
		{
			await recovery.RecoverLocalLogAsync(unreadable, cancellationToken);
			if (entry.ColdLoaded)
			{
				throw new InvalidOperationException($"The local log of stream {streamId} could not be read and was dropped; the open projection must be loaded again.", unreadable);
			}
			else
			{
				entry.Projection = _factory.Create(streamId);
				await _keeper.MaintainAsync(entry.Projection, isReplay: true, cancellationToken: cancellationToken);
			}
		}
	}
}

/// <summary>
/// The one and only replay path: reads the stream's delta after <see cref="IReplayProjection.Cursor"/> out of the
/// <see cref="IEventLog"/> and applies it, advancing the cursor. A cheap no-op when already at head, a full replay when
/// cold, and nothing at all for a host without a log.
/// </summary>
public sealed class ProjectionKeeper : IProjectionKeeper
{
	private readonly IEventLog? _log;

	public ProjectionKeeper(IEventLog? log = null)
	{
		_log = log;
	}

	public async Task MaintainAsync(
		  IReplayProjection projection
		, bool isReplay = false
		, CancellationToken cancellationToken = default
		)
	{
		if (_log is not null)
		{
			await projection.CatchUpAsync(cursor => _log.ReadStreamAsync(projection.StreamId, cursor, cancellationToken), isReplay, cancellationToken);
		}
	}
}
