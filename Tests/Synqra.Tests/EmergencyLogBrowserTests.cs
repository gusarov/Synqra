using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// In the browser EmergencyLog had no file to append to and wrote nothing at all: a replication reader's failure, a
/// refused event and a failed catch-up all vanished, and a stalled page left no trace (dev 2026-10-25).
/// </summary>
public class EmergencyLogBrowserTests
{
	[Test]
	public async Task A_browser_logger_writes_an_error_with_its_exception_to_the_console()
	{
		var console = new StringWriter();
		var logger = new EmergencyLoggerProvider(console, isOn: true).CreateLogger("Synqra.EventReplicationService");

		logger.LogError(new IndexOutOfRangeException("frame too short"), "the reader stopped on an exception; reconnecting");

		var written = console.ToString();
		await Assert.That(written).Contains("the reader stopped on an exception; reconnecting");
		await Assert.That(written).Contains("IndexOutOfRangeException");
		await Assert.That(written).Contains("frame too short");
	}

	[Test]
	public async Task A_browser_logger_keeps_per_event_chatter_off_the_console()
	{
		var console = new StringWriter();
		var logger = new EmergencyLoggerProvider(console, isOn: true).CreateLogger("Synqra.EventReplicationService");

		logger.LogInformation("<<< an event");

		await Assert.That(console.ToString()).IsEqualTo(string.Empty);
		await Assert.That(logger.IsEnabled(LogLevel.Information)).IsFalse();
		await Assert.That(logger.IsEnabled(LogLevel.Warning)).IsTrue();
	}
}
