using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
	public async Task Should_save_emergency_log()
	{
		var keyData = Guid.NewGuid().ToString();
		EmergencyLog.Default.Message("Should_save_emergency_log test " + keyData);
		await Assert.That(FileReadAllText(Path.Combine(Path.GetTempPath(), "Synqra", "Emergency.log"))).Contains(keyData);
	}

	[Test]
	public async Task Should_survive_multithread()
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
		var path = Path.Combine(Path.GetTempPath(), "Synqra", "Emergency.log");
		var log = FileReadAllText(path);
		var pathTemplate = Path.Combine(Path.GetTempPath(), "Synqra", "Emergency_{0}.log");
		for (int i = 2; ; i++)
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
		await Assert.That(log).Contains("Should_survive_multithread");
		using var _ = Assert.Multiple();
		foreach (var guid in guids)
		{
			await Assert.That(log).Contains(guid.ToString());
		}
	}

	private string ReadAllText(string fileName)
	{
		return null;
		// DO not lock same mutextes, because we just read here. But reader must share write access, like any log viewer, to avoid problems
		using var file = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
		using var sr = new StreamReader(file);
		return sr.ReadToEnd();
	}
}
