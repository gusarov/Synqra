using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Synqra.Utils.Tests;

internal class EmergencyLogForwardTests
{
	readonly StringWriter _console = new StringWriter();

	EmergencyLog CreateLog(bool isOn = true) => new EmergencyLog(new EmergencyLoggerProvider(_console, isOn));

	[Test]
	public async Task Should_hand_an_entry_to_the_host_provider_under_the_synqra_category()
	{
		var log = CreateLog();
		var host = new CapturingProvider();
		log.ForwardTo(host);
		var failure = new InvalidOperationException("boom");

		log.LogError(new EventId(7, "Seven"), failure, "the reader stopped");

		await Assert.That(host.Entries.Count).IsEqualTo(1);
		var entry = host.Entries[0];
		await Assert.That(entry.Category).IsEqualTo("Synqra");
		await Assert.That(entry.Level).IsEqualTo(LogLevel.Error);
		await Assert.That(entry.EventId.Id).IsEqualTo(7);
		await Assert.That(entry.Message).IsEqualTo("the reader stopped");
		await Assert.That(entry.Exception).IsSameReferenceAs(failure);
		await Assert.That(_console.ToString()).Contains("the reader stopped"); // the console still gets it
	}

	[Test]
	public async Task Should_stop_forwarding_after_null()
	{
		var log = CreateLog();
		var host = new CapturingProvider();
		log.ForwardTo(host);
		log.ForwardTo(null);

		log.LogWarning("after the host left");

		await Assert.That(host.Entries.Count).IsEqualTo(0);
		await Assert.That(_console.ToString()).Contains("after the host left");
	}

	[Test]
	public async Task Should_keep_the_entry_and_not_throw_when_the_host_provider_throws()
	{
		var log = CreateLog();
		log.ForwardTo(new CapturingProvider { Throws = true });

		log.LogWarning("still written");

		var console = _console.ToString();
		await Assert.That(console).Contains("still written");
		await Assert.That(console).Contains("the forwarded provider failed");
	}

	[Test]
	public async Task Should_ask_the_host_provider_about_levels_the_console_skips()
	{
		var log = CreateLog(); // a browser console keeps Warning and above only
		var host = new CapturingProvider { MinimumLevel = LogLevel.Debug };
		log.ForwardTo(host);

		log.LogDebug("detail for the host");

		await Assert.That(log.IsEnabled(LogLevel.Debug)).IsTrue();
		await Assert.That(host.Entries.Count).IsEqualTo(1);
		await Assert.That(_console.ToString()).DoesNotContain("detail for the host");
	}

	[Test]
	public async Task Should_not_recurse_when_the_host_logs_back_into_this_log()
	{
		var log = CreateLog();
		var host = new CapturingProvider();
		host.Reenter = () => log.LogWarning("logged by the host while it handles an entry"); // without the guard: a stack overflow
		log.ForwardTo(host);

		log.LogError("the reader stopped");

		await Assert.That(host.Entries.Count).IsEqualTo(1);
		await Assert.That(host.Entries[0].Message).IsEqualTo("the reader stopped");
		await Assert.That(_console.ToString()).Contains("logged by the host while it handles an entry"); // kept here instead
	}

	[Test]
	public async Task Should_forward_the_next_entry_after_a_nested_one()
	{
		var log = CreateLog();
		var host = new CapturingProvider();
		host.Reenter = () => log.LogWarning("nested");
		log.ForwardTo(host);

		log.LogError("first");
		log.LogError("second");

		await Assert.That(host.Entries.Select(entry => entry.Message)).IsEquivalentTo(new[] { "first", "second", });
	}

	[Test]
	public async Task Should_forward_again_after_the_host_threw()
	{
		var log = CreateLog();
		var host = new CapturingProvider { ThrowsOnce = true, };
		log.ForwardTo(host);

		log.LogError("lost to the throw");
		log.LogError("taken");

		await Assert.That(host.Entries.Select(entry => entry.Message)).IsEquivalentTo(new[] { "taken", });
	}

	sealed record Entry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

	sealed class CapturingProvider : ILoggerProvider
	{
		public List<Entry> Entries { get; } = new List<Entry>();

		public bool Throws { get; init; }

		public bool ThrowsOnce { get; set; }

		/// <summary>Called inside Log, before the entry is recorded: a host that logs while it handles an entry.</summary>
		public Action? Reenter { get; set; }

		public LogLevel MinimumLevel { get; init; } = LogLevel.Warning;

		public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

		public void Dispose()
		{
		}

		sealed class CapturingLogger(CapturingProvider provider, string category) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => logLevel >= provider.MinimumLevel;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				if (provider.Throws || provider.ThrowsOnce)
				{
					provider.ThrowsOnce = false;
					throw new InvalidOperationException("the host provider is broken");
				}
				provider.Reenter?.Invoke();
				provider.Entries.Add(new Entry(category, logLevel, eventId, formatter(state, exception), exception));
			}
		}
	}
}
