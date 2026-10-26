using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Synqra.Utils.Tests;

/// <summary>Off by default (it cost every caller, and a dev box held a 512 MiB file); errors still reach the host's ILogger.</summary>
internal class EmergencyLogSwitchTests
{
	readonly StringWriter _console = new StringWriter();

	[Test]
	[NotInParallel(nameof(EmergencyLog.IsOn))]
	public async Task Should_be_off_by_default()
	{
		await Assert.That(EmergencyLog.Default.IsOn).IsFalse();
		await Assert.That(EmergencyLog.Default.IsEnabled(LogLevel.Critical)).IsFalse();
	}

	[Test]
	public async Task Should_neither_format_nor_write_while_off()
	{
		var log = new EmergencyLog(new EmergencyLoggerProvider(_console, isOn: false));
		var formatted = false;

		log.Log(LogLevel.Error, default, "state", null, (state, ex) =>
		{
			formatted = true;
			return "the reader stopped";
		});

		await Assert.That(formatted).IsFalse();
		await Assert.That(_console.ToString()).IsEqualTo(string.Empty);
	}

	[Test]
	public async Task Should_write_once_turned_on()
	{
		var log = new EmergencyLog(new EmergencyLoggerProvider(_console, isOn: false));

		log.IsOn = true;
		log.LogWarning("connect attempt failed");

		await Assert.That(_console.ToString()).Contains("connect attempt failed");
	}

	[Test]
	public async Task Should_still_hand_errors_to_the_host_while_off()
	{
		var log = new EmergencyLog(new EmergencyLoggerProvider(_console, isOn: false));
		var host = new RecordingProvider();
		log.ForwardTo(host);

		log.LogError(new InvalidOperationException("frame too short"), "EventReplicationService: the reader stopped on an exception; reconnecting.");

		await Assert.That(host.Messages).Contains("EventReplicationService: the reader stopped on an exception; reconnecting.");
		await Assert.That(_console.ToString()).IsEqualTo(string.Empty);
	}

	[Test]
	[NotInParallel("Synqra.Trace.Stages")]
	public async Task Should_write_stage_tracing_while_off()
	{
		var log = new EmergencyLog(new EmergencyLoggerProvider(_console, isOn: false));
		AppContext.SetSwitch("Synqra.Trace.Stages", true);
		try
		{
			log.Stage("replication: online");
		}
		finally
		{
			AppContext.SetSwitch("Synqra.Trace.Stages", false);
		}

		await Assert.That(_console.ToString()).Contains("replication: online");
	}

	sealed class RecordingProvider : ILoggerProvider
	{
		public List<string> Messages { get; } = new List<string>();

		public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

		public void Dispose()
		{
		}

		sealed class RecordingLogger(RecordingProvider provider) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> provider.Messages.Add(formatter(state, exception));
		}
	}
}
