using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Synqra.AppendStorage.MongoDb;

/// <summary>
/// Which half of a Synqra store a registered Mongo database is — an upgrade declares which
/// roles it needs, and the runner matches them up per service key.
/// </summary>
public enum SynqraMongoDatabaseRole
{
	/// <summary>The append-only event log (source of truth).</summary>
	Events,
	/// <summary>The materialized projection / read models (rebuildable).</summary>
	Projection,
}

/// <summary>
/// One Mongo database a Synqra DI extension opened, announced for the upgrade runner. Every
/// extension that opens a database (<c>AddAppendStorageMongoDb</c>, <c>AddMongoDbSynqraStore</c>)
/// contributes one of these — that is what makes upgrades fully automatic: Synqra upgrades
/// whatever storage it opens, and a host never wires anything. The database is a factory so
/// nothing connects until the runner actually starts.
/// </summary>
public sealed record SynqraMongoUpgradeParticipant(
	  string? ServiceKey
	, SynqraMongoDatabaseRole Role
	, Func<IServiceProvider, IMongoDatabase> Database
	, string? EventsCollectionName = null
	);

/// <summary>A single, versioned, idempotent storage upgrade the runner can apply.</summary>
internal interface ISynqraMongoUpgrade
{
	/// <summary>Stable identity — the claim key in <c>_synqra_upgrades</c>. Never reuse or rename.</summary>
	string Id { get; }

	Task RunAsync(IMongoDatabase events, string eventsCollectionName, IMongoDatabase? projection, ILogger logger, CancellationToken cancellationToken);
}

/// <summary>
/// Runs every applicable storage upgrade at host startup, before the app serves traffic —
/// registered automatically by the same DI extensions that open the databases, so consumers
/// never call anything. Multi-node safe: an upgrade is claimed by inserting its
/// <see cref="ISynqraMongoUpgrade.Id"/> as <c>_id</c> into the <c>_synqra_upgrades</c>
/// collection of the events database being upgraded — the unique index on <c>_id</c>
/// guarantees exactly one node wins the insert and performs the upgrade; every other node
/// waits for the winner's completion marker. A crashed winner's claim (no completion marker
/// after a generous stale window) is taken over by deleting the stale claim and re-claiming.
/// A failed or timed-out upgrade fails startup on purpose: booting into a store the readers
/// cannot correctly interpret is worse than not booting.
/// </summary>
public sealed class SynqraMongoUpgradeService(
	  IEnumerable<SynqraMongoUpgradeParticipant> participants
	, IServiceProvider serviceProvider
	, ILogger<SynqraMongoUpgradeService>? logger = null
	) : IHostedService
{
	const string UpgradesCollectionName = "_synqra_upgrades";
	static readonly TimeSpan CompletionWaitTimeout = TimeSpan.FromMinutes(5);
	static readonly TimeSpan StaleClaimAge = TimeSpan.FromMinutes(15);
	static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

	internal static readonly ISynqraMongoUpgrade[] Upgrades =
	[
		new EventSidBackfillUpgrade(),
	];

	readonly ILogger _logger = logger ?? NullLogger<SynqraMongoUpgradeService>.Instance;

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		// Group what was opened by service key: a process hosting several independent
		// Synqra-backed features upgrades each feature's own databases separately. Duplicate
		// contributions (an extension called twice) collapse — same key+role is the same DB.
		foreach (var group in participants.GroupBy(p => p.ServiceKey))
		{
			var events = group.FirstOrDefault(p => p.Role == SynqraMongoDatabaseRole.Events);
			if (events is null)
			{
				continue; // nothing upgradeable without an event log
			}
			var projection = group.FirstOrDefault(p => p.Role == SynqraMongoDatabaseRole.Projection);

			var eventsDb = events.Database(serviceProvider);
			// Probe before claiming, with a short timeout. An UNREACHABLE database (server gone,
			// wrong credentials — e.g. a test host that configures a store it never touches) must
			// not fail startup: nothing can read an unreachable store, so there is nothing the
			// upgrade needs to protect, and before upgrades existed such a host booted fine
			// because Mongo connects lazily. Only a REACHABLE store with a FAILING upgrade is
			// fatal (see RunOnceAsync) — that one would otherwise serve misinterpretable data.
			if (!await IsReachableAsync(eventsDb, cancellationToken))
			{
				_logger.LogError("Storage upgrades skipped for '{Database}' (service key '{Key}'): events database unreachable — the store will fail on first use instead.", eventsDb.DatabaseNamespace.DatabaseName, group.Key);
				continue;
			}
			var projectionDb = projection?.Database(serviceProvider);
			if (projectionDb is not null && !await IsReachableAsync(projectionDb, cancellationToken))
			{
				_logger.LogWarning("Projection database '{Database}' unreachable — upgrades run without the projection-derived fallback.", projectionDb.DatabaseNamespace.DatabaseName);
				projectionDb = null;
			}
			foreach (var upgrade in Upgrades)
			{
				await RunOnceAsync(upgrade, eventsDb, events.EventsCollectionName ?? "Event", projectionDb, cancellationToken);
			}
		}
	}

	static async Task<bool> IsReachableAsync(IMongoDatabase database, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));
		try
		{
			await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), readPreference: null, timeout.Token);
			return true;
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
	}

	async Task RunOnceAsync(ISynqraMongoUpgrade upgrade, IMongoDatabase eventsDb, string eventsCollectionName, IMongoDatabase? projectionDb, CancellationToken cancellationToken)
	{
		var claims = eventsDb.GetCollection<BsonDocument>(UpgradesCollectionName);
		var started = DateTime.UtcNow;
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var claimedAt = DateTime.UtcNow;
			try
			{
				await claims.InsertOneAsync(new BsonDocument
				{
					["_id"] = upgrade.Id,
					["claimedAt"] = claimedAt,
					["claimedBy"] = Environment.MachineName,
				}, options: null, cancellationToken);
			}
			catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
			{
				// Someone else holds (or held) the claim — wait for their completion marker.
				var winner = await WaitForCompletionAsync(claims, upgrade.Id, cancellationToken);
				if (winner is not null && winner.Contains("completedAt"))
				{
					return; // already upgraded (now, or on some earlier deployment)
				}
				// No completion and the claim is stale — the winner most likely crashed
				// mid-upgrade (the upgrade itself is idempotent, so re-running is safe).
				// Delete exactly the stale claim we observed (claimedAt match keeps two
				// takeover attempts from double-deleting a fresh claim) and re-claim.
				if (winner is not null)
				{
					await claims.DeleteOneAsync(
						  Builders<BsonDocument>.Filter.Eq("_id", upgrade.Id)
						& Builders<BsonDocument>.Filter.Eq("claimedAt", winner["claimedAt"])
						& Builders<BsonDocument>.Filter.Exists("completedAt", false)
						, cancellationToken);
				}
				continue;
			}

			_logger.LogInformation("Storage upgrade '{Upgrade}' claimed on '{Database}' — running.", upgrade.Id, eventsDb.DatabaseNamespace.DatabaseName);
			await upgrade.RunAsync(eventsDb, eventsCollectionName, projectionDb, _logger, cancellationToken);
			await claims.UpdateOneAsync(
				  Builders<BsonDocument>.Filter.Eq("_id", upgrade.Id)
				, Builders<BsonDocument>.Update.Set("completedAt", DateTime.UtcNow)
				, options: null, cancellationToken);
			_logger.LogInformation("Storage upgrade '{Upgrade}' completed on '{Database}'.", upgrade.Id, eventsDb.DatabaseNamespace.DatabaseName);
			return;
		}
	}

	/// <summary>Polls the claim doc until it carries <c>completedAt</c>, the claim goes stale, or the
	/// wait times out (which fails startup — see class remarks). Returns the last observed doc.</summary>
	async Task<BsonDocument?> WaitForCompletionAsync(IMongoCollection<BsonDocument> claims, string upgradeId, CancellationToken cancellationToken)
	{
		var waitStarted = DateTime.UtcNow;
		while (true)
		{
			var doc = await claims.Find(Builders<BsonDocument>.Filter.Eq("_id", upgradeId)).FirstOrDefaultAsync(cancellationToken);
			if (doc is null || doc.Contains("completedAt"))
			{
				return doc; // gone (stale claim deleted by another waiter) or done
			}
			if (doc["claimedAt"].ToUniversalTime() < DateTime.UtcNow - StaleClaimAge)
			{
				return doc; // stale — caller takes over
			}
			if (DateTime.UtcNow - waitStarted > CompletionWaitTimeout)
			{
				throw new TimeoutException($"Storage upgrade '{upgradeId}' is still running on another node after {CompletionWaitTimeout} — refusing to serve a store mid-upgrade.");
			}
			_logger.LogInformation("Storage upgrade '{Upgrade}' is running on another node — waiting.", upgradeId);
			await Task.Delay(PollInterval, cancellationToken);
		}
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
