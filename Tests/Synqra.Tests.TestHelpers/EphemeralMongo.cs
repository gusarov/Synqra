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
static class EphemeralMongo
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
							/*
							try
							{
								SweepStaleMongodOnWindows();
								_runner = MongoDbRunner.Start(singleNodeReplSet: false);
								_engineConnectionString = _runner.ConnectionString;
							}
							catch (Exception ex)
							{
								EmergencyLog.Default.LogError(ex, $"MongoDbRunner");
								throw;
							}
							*/
							throw new Exception("ConnectionString 'mongodb' is required for test to run. Make sure spawning mongod and setting env var is part of test execution command");
						}

						// Maintenance - All databases named as a v7 guid
						using var client = new MongoClient(_engineConnectionString);
						var now = DateTime.UtcNow;
						foreach (var databaseName in client.ListDatabaseNames().ToAsyncEnumerable().ToBlockingEnumerable().ToArray())
						{
							if (databaseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
							{
								if (Guid.TryParse(databaseName[prefix.Length..], out var guid) && guid.GetVersion() == 7 && (now - guid.GetTimestamp()).TotalMinutes > 1)
								{
									client.DropDatabase(databaseName);
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

			var builder = new MongoUrlBuilder(_engineConnectionString)
			{
				DatabaseName = prefix + GuidExtensions.CreateVersion7().ToString("N").ToLowerInvariant(),
			};
			return builder.ToString();
		}
	}

	[Conditional("DEBUG")]
	static void SweepStaleMongodOnWindows()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return;
		}
		// A never-disposed shared runner can leave a mongod behind across runs on dev
		// machines; sweep ones older than a minute (never elevated, so other users'
		// processes are left alone).
		foreach (var p in Process.GetProcessesByName("mongod").Concat(Process.GetProcessesByName("mongod.exe")))
		{
			try
			{
				if (DateTime.UtcNow - p.StartTime.ToUniversalTime() > TimeSpan.FromMinutes(1))
				{
					p.Kill();
				}
			}
			catch (System.ComponentModel.Win32Exception)
			{
				// Owned by another user — skip, let Mongo2Go work around it.
			}
		}
	}
}
