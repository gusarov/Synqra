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

namespace Synqra.Tests;

internal class EmergencyLoggerTests : BaseTest
{
	/*
	[Test]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_do_nothing_for_non_configured_emergency_log()
	{
		var ops = MeasureOps(() => SynqraEmergencyLog.Default.LogMessage("This is a test message"));
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

	[Test]
	public async Task Should_20_handle_locked_files()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			// File locking is not supported on Linux and MacOS
			return;
		}
		var keyData = Guid.NewGuid().ToString();
		var path = Path.Combine(Path.GetTempPath(), "Synqra", "Emergency.log");

		EmergencyLog.Default.Message("Prepare file");
		using var file = File.OpenRead(path); // forgot to share!! So this should cause EmergencyLog to create new file
		file.Lock(0, 0); // Lock whole file for this stream only, to prevent other writers to corrupt the log. Readers are still allowed.

		EmergencyLog.Default.Message("Should_handle_locked_files test " + keyData);

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
				EmergencyLog.Default.Message("Should_survive_multithread test " + guid);
			}
		}
		await Task.WhenAll(Thread(), Thread(), Thread(), Thread(), Thread());
		var log = ReadAllLogs();
		await Assert.That(log).Contains("Should_survive_multithread");
		using var _ = Assert.Multiple();
		foreach (var guid in guids)
		{
			await Assert.That(log).Contains(guid.ToString());
		}
	}
}
