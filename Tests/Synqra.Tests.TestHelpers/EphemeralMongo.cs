using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Synqra;

namespace Synqra.Tests.TestHelpers;

/// <summary>
/// One shared ephemeral <c>mongod</c> per test process, provided by Mongo2Go — the same
/// mechanism Quotaly's <c>IntegrationTestBase</c> uses. The runner is started lazily on
/// first use and reused across tests/fixtures (never disposed per-test: Mongo2Go's
/// finalizer reaps it at process exit, and stale mongod on Windows dev boxes is swept
/// first). Per-test isolation comes from a unique database name, not a new runner —
/// per-test runners are slow and prone to port-rebind flakiness.
/// <para>
/// Standalone (not a replica set): the append-storage path does plain inserts/finds with
/// no multi-document transactions, so it doesn't need the replica set the app-level base
/// starts; standalone is faster and more reliable.
/// </para>
/// </summary>
public static class EphemeralMongo
{
	static readonly object _sync = new();
	static string? _engineConnectionString;

	// Every database name this process handed out, so we can drop them all on ProcessExit
	// instead of leaving temp_minute_* dbs behind for a future run's sweep to (eventually)
	// reap. This is the cheap, cross-platform equivalent of "schedule a remover for every
	// new db": a normal test-host shutdown reaps this run's databases immediately, while the
	// first-read stale sweep above still covers processes that were killed (Ctrl-C / Stop
	// Debugging) before ProcessExit could fire.
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _handedOutDatabases = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Every read of this property generates mongo url with new temporary database
	/// </summary>
	public static string? ConnectionString
	{
		get
		{
			const string prefix = "temp_minute_";
			if (_engineConnectionString is null)
			{
				lock (_sync)
				{
					if (_engineConnectionString is null)
					{
						var app = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
						{
							EnvironmentName = Environments.Development,
						});
						app.Configuration.AddUserSecrets(typeof(EphemeralMongo).Assembly);
						_engineConnectionString = app.Configuration.GetConnectionString("mongodb");
						if (string.IsNullOrWhiteSpace(_engineConnectionString))
						{
							throw new Exception("ConnectionString 'mongodb' is required for test to run. Make sure spawning mongod and setting env var is part of test execution command");
						}

						AppDomain.CurrentDomain.ProcessExit += static (_, _) => DropHandedOutDatabases();

						// Maintenance - All databases named as a v7 guid
						using var client = new MongoClient(_engineConnectionString);
						var utcNow = DateTime.UtcNow;
						foreach (var databaseName in client.ListDatabaseNames().ToAsyncEnumerable().ToBlockingEnumerable().ToArray())
						{
							if (databaseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
							{
								if (Guid.TryParse(databaseName[prefix.Length..].Replace('_', '-'), out var guid) && guid.GetVersion() == 7)
								{
									if ((utcNow - guid.GetTimestamp()).TotalMinutes > 1)
									{
										client.DropDatabase(databaseName);
									}
								}
								else
								{
									var marker = client.GetDatabase(databaseName).GetCollection<MongoDB.Bson.BsonDocument>("_xkit");
									var stamp = marker.Find(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", "EphemeralMongoStamp")).FirstOrDefault();
									if (stamp is null)
									{
										try
										{
											marker.InsertOne(new MongoDB.Bson.BsonDocument { ["_id"] = "EphemeralMongoStamp", ["seen"] = utcNow });
										}
										catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
										{
											// Another concurrent test process (net8/net9/net10 runs overlap) stamped it
											// first — fine, the next sweep compares against whatever timestamp won.
										}
									}
									else if ((utcNow - stamp["seen"].ToUniversalTime()).TotalMinutes > 1)
									{
										client.DropDatabase(databaseName);
									}
								}
							}
						}
					}
				}
			}

			// Ephemeral Mongo never contain dbname in constr
			// Config might have it but it is ignored.
			// Instead, every read of this field would give new random db name
			// And first read will recycle the temp dbs

			var newDatabaseName = prefix + GuidExtensions.CreateVersion7().ToString().Replace('-', '_').ToLowerInvariant();
			// For testing purposes I'm randomizing it - maintenance still should work stateless
			// var newDatabaseName = prefix + Guid.NewGuid().ToString("N");

			// Remember it so ProcessExit can drop it (see _handedOutDatabases).
			_handedOutDatabases[newDatabaseName] = 0;

			var builder = new MongoUrlBuilder(_engineConnectionString)
			{
				DatabaseName = newDatabaseName,
			};
			return builder.ToString();
		}
	}

	/// <summary>
	/// Drops every database this process handed out. Best-effort and fast: it runs on
	/// ProcessExit (a normal <c>dotnet test</c> shutdown), where only a couple of seconds of
	/// budget is available, so connect/server-selection timeouts are kept short and every
	/// failure is swallowed — anything that survives is reaped by the next run's stale sweep.
	/// </summary>
	static void DropHandedOutDatabases()
	{
		if (_engineConnectionString is null || _handedOutDatabases.IsEmpty)
		{
			return;
		}

		try
		{
			var settings = MongoClientSettings.FromConnectionString(_engineConnectionString);
			settings.ConnectTimeout = TimeSpan.FromSeconds(2);
			settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
			var client = new MongoClient(settings);

			foreach (var databaseName in _handedOutDatabases.Keys)
			{
				try
				{
					client.DropDatabase(databaseName);
					_handedOutDatabases.TryRemove(databaseName, out _);
				}
				catch
				{
					// mongod may already be gone (service stop) or unreachable — leave the
					// name behind and let the next run's stale sweep reap it.
				}
			}
		}
		catch
		{
			// Couldn't even build a client (engine already torn down) — nothing to do.
		}
	}
}
