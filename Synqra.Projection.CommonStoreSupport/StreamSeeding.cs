using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Synqra;

/// <summary>
/// Runs the registered <see cref="IStreamSeeder"/>s on a stream proven new, and on no other (docs/model.md §10). Its creator
/// seeds a stream it created (<see cref="SeedCreatedAsync"/>, offline as well); the master seeds a stream whose log it finds
/// empty (<see cref="SeedIfEmptyAsync"/>). A client that only starts with an empty local log has neither proof, and no
/// replication path calls this on a client.
/// </summary>
public sealed class StreamSeeding
{
	readonly IStreamSeeder[] _seeders;

	// One seed at a time in this process: the emptiness check and the seed's commit happen under it, so two callers never both see an empty log.
	readonly SemaphoreSlim _gate = new(1, 1);

	// Streams this process has seeded or found holding events: a log never empties, so they are not read again.
	readonly ConcurrentDictionary<Guid, byte> _settled = new();

	public StreamSeeding([FromKeyedServices] IEnumerable<IStreamSeeder> seeders)
	{
		_seeders = seeders.ToArray();
	}

	/// <summary>Whether any seeder is registered: with none, nothing is ever read or written.</summary>
	public bool HasSeeders => _seeders.Length > 0;

	/// <summary>
	/// The master's seed: runs the seeders when the stream's log holds no event, and says whether it did. Call it before reading
	/// the stream for a subscriber, so the subscriber's replay carries the seed.
	/// </summary>
	public async Task<bool> SeedIfEmptyAsync(Guid streamId, IEventLog log, IObjectStore store, CancellationToken cancellationToken = default)
	{
		var seeded = false;
		if (HasSeeders && !_settled.ContainsKey(streamId))
		{
			await _gate.WaitAsync(cancellationToken);
			try
			{
				if (!_settled.ContainsKey(streamId))
				{
					if (!await HoldsEventsAsync(log, streamId, cancellationToken))
					{
						await RunSeedersAsync(new StreamSeedContext { StreamId = streamId, Origin = StreamOrigin.EmptyOnMaster }, store);
						seeded = true;
					}
					_settled.TryAdd(streamId, 0);
				}
			}
			finally
			{
				_gate.Release();
			}
		}
		return seeded;
	}

	/// <summary>
	/// The master's seeding of a stream a connection subscribes to: seeds it when its log is empty; otherwise evolves an older seed to
	/// each seeder's current <see cref="IStreamSeeder.Version"/>. Call it before reading the stream for the subscriber, so the replay
	/// carries what it wrote. A stream is checked once per process; an upgrade that fails is tried again by the next subscriber.
	/// </summary>
	public async Task<StreamSeedOutcome> SeedOrUpgradeAsync(Guid streamId, IEventLog log, IObjectStore store, CancellationToken cancellationToken = default)
	{
		var outcome = StreamSeedOutcome.None;
		if (HasSeeders && !_settled.ContainsKey(streamId))
		{
			await _gate.WaitAsync(cancellationToken);
			try
			{
				if (!_settled.ContainsKey(streamId))
				{
					if (await HoldsEventsAsync(log, streamId, cancellationToken))
					{
						outcome = await UpgradeSeedsAsync(new StreamSeedContext { StreamId = streamId, Origin = StreamOrigin.PopulatedOnMaster }, store)
							? StreamSeedOutcome.Upgraded
							: StreamSeedOutcome.None;
					}
					else
					{
						await RunSeedersAsync(new StreamSeedContext { StreamId = streamId, Origin = StreamOrigin.EmptyOnMaster }, store);
						outcome = StreamSeedOutcome.Seeded;
					}
					_settled.TryAdd(streamId, 0);
				}
			}
			finally
			{
				_gate.Release();
			}
		}
		return outcome;
	}

	/// <summary>
	/// The creator's seed: runs the seeders on a stream this node has just created. A stream whose log already holds events was not
	/// created here, and is refused.
	/// </summary>
	/// <exception cref="StreamNotNewException">The stream's log already holds events.</exception>
	public async Task SeedCreatedAsync(Guid streamId, IEventLog log, IObjectStore store, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (await HoldsEventsAsync(log, streamId, cancellationToken))
			{
				throw new StreamNotNewException(streamId);
			}
			else
			{
				await RunSeedersAsync(new StreamSeedContext { StreamId = streamId, Origin = StreamOrigin.CreatedHere }, store);
				_settled.TryAdd(streamId, 0);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	async Task RunSeedersAsync(StreamSeedContext context, IObjectStore store)
	{
		using (SynqraStreamContext.Enter(context.StreamId)) // a multitenant store writes into the stream in scope
		{
			foreach (var seeder in _seeders)
			{
				try
				{
					await seeder.SeedAsync(context, store);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					throw new StreamSeedingException($"Seeder {seeder.GetType().FullName} failed on stream {context.StreamId} ({context.Origin}).", ex);
				}
			}
		}
	}

	// Each seeder says where its seed stands in this stream; one behind its version is brought up to it.
	async Task<bool> UpgradeSeedsAsync(StreamSeedContext context, IObjectStore store)
	{
		var upgraded = false;
		using (SynqraStreamContext.Enter(context.StreamId)) // a multitenant store reads and writes the stream in scope
		{
			foreach (var seeder in _seeders)
			{
				try
				{
					if (await seeder.ReadVersionAsync(context, store) is int from && from < seeder.Version)
					{
						await seeder.UpgradeAsync(context, store, from);
						upgraded = true;
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					throw new StreamSeedingException($"Seeder {seeder.GetType().FullName} failed to evolve stream {context.StreamId} to version {seeder.Version}.", ex);
				}
			}
		}
		return upgraded;
	}

	async Task<bool> HoldsEventsAsync(IEventLog log, Guid streamId, CancellationToken cancellationToken)
	{
		var holds = false;
		await foreach (var _ in log.ReadStreamAsync(streamId, default, cancellationToken))
		{
			holds = true;
			break; // the first event answers it
		}
		return holds;
	}
}
