using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Synqra.Tests.Multitarget;

internal class EmergencyLoggerTests : BaseTest
{
	[Test]
	[Ignore("Decided to make it stateless, so no way to enable/disable logger, it is always doing something")]
	public void Should_do_nothing_for_non_configured_emergency_log()
	{
		var ops = MeasureOps(() => SynqraEmergencyLog.Default.LogMessage("This is a test message"));
		Assert.IsTrue(ops > 10_000_000, $"Logging should be noop");
	}

	[Test]
	public void Should_save_emergency_log()
	{
		var keyData = Guid.NewGuid().ToString();
		SynqraEmergencyLog.Default.LogMessage("Should_save_emergency_log test " + keyData);
		Assert.IsTrue(File.ReadAllText(Path.Combine(Path.GetTempPath(), "SynqraEmergency.log")).Contains(keyData));
	}
}
