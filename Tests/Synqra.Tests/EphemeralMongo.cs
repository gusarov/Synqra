using System.Diagnostics;
using System.Runtime.InteropServices;
using Mongo2Go;

namespace Synqra.Tests;

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
	static MongoDbRunner? _runner;
	static string? _skipReason;

	/// <summary>The shared connection string, or <c>null</c> if mongod could not start.</summary>
	public static string? ConnectionString
	{
		get
		{
			if (_runner is not null)
			{
				return _runner.ConnectionString;
			}
			if (_skipReason is not null)
			{
				return null;
			}
			lock (_sync)
			{
				if (_runner is not null)
				{
					return _runner.ConnectionString;
				}
				if (_skipReason is not null)
				{
					return null;
				}
				try
				{
					SweepStaleMongodOnWindows();
					_runner = MongoDbRunner.Start(singleNodeReplSet: false);
					return _runner.ConnectionString;
				}
				catch (Exception ex)
				{
					_skipReason = "Bundled mongod could not start in this environment: " + ex.Message;
					return null;
				}
			}
		}
	}

	/// <summary>Why the tests are skipping, when <see cref="ConnectionString"/> is null.</summary>
	public static string SkipReason => _skipReason ?? "Ephemeral mongod is unavailable";

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
