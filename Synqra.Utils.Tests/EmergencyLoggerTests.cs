using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.AssertionBuilders.Wrappers;

namespace Synqra.Utils.Tests;

internal class EmergencyLoggerTests : BaseTest
{
	/*
	[Test]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_do_nothing_for_non_configured_emergency_log()
	{
		var ops = MeasureOps(() => EmergencyLog.Default.LogInformation("This is a test message"));
		await Assert.That(ops).IsGreaterThan(10_000_000);
	}
	*/

	[Test]
	public async Task Should_10_save_emergency_log()
	{
		var keyData = Guid.NewGuid().ToString();
		EmergencyLog.Default.Message("Should_10_save_emergency_log test " + keyData);
		Console.WriteLine(keyData);
		await Assert.That(ReadAllLogs()).Contains(keyData);
	}

	string ReadAllLogs()
	{
		/*
#if NET9_0_OR_GREATER
		return string.Join("", Bro.Viewer.BroViewer.Default.ReadAllLogs());
#endif
		*/
		var path = Path.Combine(Path.GetTempPath(), "Synqra", "Emergency.log");
		var log = FileReadAllText(path);
		var pathTemplate = Path.Combine(Path.GetTempPath(), "Synqra", "Emergency_{0}.log");
		for (int i = 2; ; i++) // consider all rollovers
		{
			var fi = new FileInfo(string.Format(pathTemplate, i));
			if (fi.Exists && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours < 1)
			{
				log = FileReadAllText(fi.FullName) + log;
			}
			else
			{
				break;
			}

		}
		for (int i = 0; ; i++) // consider all locked failures caused by other tests that checks locked file handling
		{
			var fi = new FileInfo(string.Format(pathTemplate, "Locked_" + i));
			if (fi.Exists && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours < 1)
			{
				log = FileReadAllText(fi.FullName) + log;
			}
			else
			{
				break;
			}
		}
		return log;
	}

	List<string> ReadAllLines()
	{
		/*
#if NET9_0_OR_GREATER
		return string.Join("", Bro.Viewer.BroViewer.Default.ReadAllLogs());
#endif
		*/
		var path = Path.Combine(Path.GetTempPath(), "Synqra", "Emergency.log");
		var log = FileReadAllLines(path);
		var pathTemplate = Path.Combine(Path.GetTempPath(), "Synqra", "Emergency_{0}.log");
		for (int i = 2; ; i++) // consider all rollovers
		{
			var fi = new FileInfo(string.Format(pathTemplate, i));
			if (fi.Exists && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours < 1)
			{
				log.AddRange(FileReadAllLines(fi.FullName));
			}
			else
			{
				break;
			}

		}
		for (int i = 0; ; i++) // consider all locked failures caused by other tests that checks locked file handling
		{
			var fi = new FileInfo(string.Format(pathTemplate, "Locked_" + i));
			if (fi.Exists && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours < 1)
			{
				log.AddRange(FileReadAllLines(fi.FullName));
			}
			else
			{
				break;
			}
		}
		return log;
	}

	[Test]
	public async Task Should_11_format_ilog_properly()
	{
		var keyData = Guid.NewGuid().ToString();
		ServiceCollection.AddEmergencyLogger();
		var logger = ServiceProvider.GetRequiredService<ILogger<EmergencyLoggerTests>>();
		logger.LogWarning(new EventId(1001, "TheTestEeventId"), "Should_11_format_ilog_properly " + keyData);
		var log = ReadAllLines();
		var line = log.FirstOrDefault(l => l.Contains(keyData));
		Console.WriteLine(line);
		await Assert.That(line).Contains(" [WRN] ");
		await Assert.That(line).Contains(" [S.U.T.EmergencyLoggerTests] ");
	}

	[Test]
	[Skip("This test no longer make sense, because file is always locked")]
	public async Task Should_20_handle_locked_files()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			// File locking is not supported on Linux and MacOS
			return;
		}
		var keyData = Guid.NewGuid().ToString("N");
		var path = Path.Combine(Path.GetTempPath(), "Synqra", "Emergency.log");

		// EmergencyLog.Default.LogInformation("Prepare file");
		using var file = File.OpenRead(path); // forgot to share!! So this should cause EmergencyLog to create new file
		file.Lock(0, 0); // Lock whole file for this stream only, to prevent other writers to corrupt the log. Readers are still allowed.

		EmergencyLog.Default.LogInformation("Should_handle_locked_files test " + keyData);

		var pathLockedAvoidance = Path.Combine(Path.GetTempPath(), "Synqra", "Emergency_Locked_0.log");
		await Assert.That(FileReadAllText(pathLockedAvoidance)).Contains(keyData);
	}

	[Test]
	public async Task Should_30_survive_multithread()
	{
		// NOTE: This test can fail if file rolls over. This will stay like this until I change my mind about rolling over between 2 files instead of one.
		// Greenifying this test is considered as the only good reason to change the rollover logic to 2 files.
		// And honestly it does not feel fair that EmergencyLog is as reliable as it claims. It should be as a black box for flight crash research. Chances are, people log things that can happen once a month mysteriously and hard to reproduce... And what you will say? File is too big and have to be deleted?

		ConcurrentBag<Guid> guids = new ConcurrentBag<Guid>();

		async Task Thread()
		{
			for (int i = 0; i < 1_000; i++)
			{
				var guid = Guid.NewGuid();
				guids.Add(guid);
				EmergencyLogImplementation.Default.Message("Should_survive_multithread test " + guid);
			}
		}
		await Task.WhenAll(Thread(), Thread(), Thread(), Thread(), Thread());
		var log = ReadAllLogs();
		await Assert.That(log).Contains("Should_survive_multithread");
		// using var _ = Assert.Multiple();
		EmergencyLogImplementation.Default.Message("Should_survive_multithread verifying...");

		foreach (var guid in guids)
		{
			await Assert.That(log).Contains(guid.ToString());
		}
		EmergencyLogImplementation.Default.Message("Should_survive_multithread verifying done.");
	}

	// before improvements: 2 000 - 6 000

	[Test]
	[Category("Performance")]
	public async Task Should_40_measure_single_thread_performance()
	{
		var ops = MeasureOps(() => EmergencyLogImplementation.Default.Message("This is a test message " + new string('.', Random.Shared.Next(10))), new PerformanceParameters
		{
			// MaxAcceptableDeviationFactor = 1000,
			// DeviationMeasurementBatches = 7,
			MaxAcceptableDeviationFactor = 1000,
			BatchTime = TimeSpan.FromSeconds(.5),
		});
		await Assert.That(ops).IsGreaterThan(100_000);
		ops = MeasureOps(() => EmergencyLog.Default.LogInformation("This is a test message " + new string('.', Random.Shared.Next(10))), new PerformanceParameters
		{
			// MaxAcceptableDeviationFactor = 1000,
			// DeviationMeasurementBatches = 7,
			MaxAcceptableDeviationFactor = 1000,
			BatchTime = TimeSpan.FromSeconds(.5),
		});
		await Assert.That(ops).IsGreaterThan(100_000);
	}

	[Test]
	[Category("Performance")]
	public async Task Should_40_measure_concurrent_performance()
	{
		//ConcurrentBag<Guid> guids = new ConcurrentBag<Guid>();

		async Task Thread()
		{
			MeasurePerformance(() =>
			{
				var guid = Guid.NewGuid();
				//guids.Add(guid);
				EmergencyLog.Default.Message("Should_40_measure_concurrent_performance test " + guid);
			}, new PerformanceParameters
			{
				MaxAcceptableDeviationFactor = 1000,
			});
		}
		await Task.WhenAll(Thread(), Thread(), Thread());
		/*
		var log = ReadAllLogs();
		await Assert.That(log).Contains("Should_40_measure_concurrent_performance test " + guids.Count);
		// using var _ = Assert.Multiple();
		foreach (var guid in guids)
		{
			await Assert.That(log).Contains(guid.ToString());
		}
		*/
	}
}
