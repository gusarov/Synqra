using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
		await Assert.That(File.ReadAllText(Path.Combine(Path.GetTempPath(), "SynqraEmergency.log"))).Contains(keyData);
	}
}
