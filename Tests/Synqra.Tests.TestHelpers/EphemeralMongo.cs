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
/// Must be a replica set: every Mongo write of the projection runs in a transaction, which a
/// standalone mongod refuses. A server started with <c>--replSet</c> (CI's <c>withmongo</c>, a
/// local service with <c>replSetName</c> in its config) is initiated here on first use; a plain
/// standalone fails fast with that instruction.
/// </para>
/// </summary>
public static class EphemeralMongo
{
	static readonly object _sync = new();
	static string? _engineConnectionString;

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

						new ReplicaSetInitiator(_engineConnectionString).EnsureInitiated();

						// Maintenance - drop the temp databases of earlier runs
						using var client = new MongoClient(_engineConnectionString);
						new TempDatabaseSweeper(client, prefix).Sweep(DateTime.UtcNow);
					}
				}
			}

			// Ephemeral Mongo never contain dbname in constr
			// Config might have it but it is ignored.
			// Instead, every read of this field would give new random db name
			// And first read will recycle the temp dbs

			var builder = new MongoUrlBuilder(_engineConnectionString)
			{
				DatabaseName = prefix + GuidExtensions.CreateVersion7().ToString().Replace('-', '_').ToLowerInvariant(),
				// For testing purposes I'm randomizing it - maintenance still should work stateless
				// DatabaseName = prefix + Guid.NewGuid().ToString("N"),
			};
			return builder.ToString();
		}
	}
}
