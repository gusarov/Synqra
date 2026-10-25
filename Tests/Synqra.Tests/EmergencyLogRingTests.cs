using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// A tab frozen mid IndexedDB transaction can't show its console, and IndexedDB is what it blocks: its last lines go to
/// localStorage, one small key per line, so another tab or a reload can read them (2026-10-25).
/// </summary>
public class EmergencyLogRingTests
{
	[Test]
	public async Task The_ring_keeps_its_last_lines_in_sequence_order()
	{
		var storage = new MemoryLogStorage();
		var ring = new EmergencyLogRing(storage, new StringWriter(), capacity: 3);

		for (var i = 0; i < 5; i++)
		{
			ring.Append($"line {i}");
		}

		var lines = ring.ReadLines().ToArray();
		await Assert.That(lines.Length).IsEqualTo(3);
		await Assert.That(lines[0]).StartsWith("2 ");
		await Assert.That(lines[0]).EndsWith($" {ring.Tab} line 2");
		await Assert.That(lines[1]).EndsWith(" line 3");
		await Assert.That(lines[2]).EndsWith(" line 4");
	}

	[Test]
	public async Task A_line_is_one_slot_and_the_head()
	{
		var storage = new MemoryLogStorage();
		var ring = new EmergencyLogRing(storage, new StringWriter(), capacity: 3);

		for (var i = 0; i < 5; i++)
		{
			ring.Append($"line {i}");
		}

		await Assert.That(storage.Items.Count).IsEqualTo(4);
		await Assert.That(storage.Items["synqra.log.head"]).IsEqualTo("5");
		await Assert.That(storage.Items["synqra.log.1"]).EndsWith(" line 4");
		await Assert.That(storage.Writes).IsEqualTo(10);
	}

	[Test]
	public async Task Tabs_share_one_ring_and_keep_their_order()
	{
		var storage = new MemoryLogStorage();
		var frozen = new EmergencyLogRing(storage, new StringWriter());
		var reader = new EmergencyLogRing(storage, new StringWriter());

		frozen.Append("frozen 0");
		reader.Append("reader 0");
		frozen.Append("frozen 1");

		var lines = reader.ReadLines().ToArray();
		await Assert.That(frozen.Tab).IsNotEqualTo(reader.Tab);
		await Assert.That(lines.Length).IsEqualTo(3);
		await Assert.That(lines[0]).EndsWith($" {frozen.Tab} frozen 0");
		await Assert.That(lines[1]).EndsWith($" {reader.Tab} reader 0");
		await Assert.That(lines[2]).EndsWith($" {frozen.Tab} frozen 1");
	}

	[Test]
	public async Task A_refusing_storage_is_reported_once_with_its_exception_and_logging_carries_on()
	{
		var storage = new MemoryLogStorage { Refusal = new InvalidOperationException("QuotaExceededError") };
		var console = new StringWriter();
		var ring = new EmergencyLogRing(storage, console);

		ring.Append("first");
		ring.Append("second");

		var written = console.ToString();
		await Assert.That(written).Contains("localStorage refused a line");
		await Assert.That(written).Contains("QuotaExceededError");
		await Assert.That(written.IndexOf("localStorage refused a line")).IsEqualTo(written.LastIndexOf("localStorage refused a line"));
	}

	[Test]
	public async Task A_long_line_is_cut()
	{
		var storage = new MemoryLogStorage();
		var ring = new EmergencyLogRing(storage, new StringWriter(), maxTextLength: 10);

		ring.Append(new string('x', 100));

		await Assert.That(ring.ReadLines().Single()).EndsWith($" {ring.Tab} xxxxxxxxxx…");
	}

	[Test]
	public async Task A_browser_logger_keeps_warnings_in_the_ring_and_not_per_event_chatter()
	{
		var storage = new MemoryLogStorage();
		var ring = new EmergencyLogRing(storage, new StringWriter());
		var logger = new EmergencyLogger("Synqra.EventReplicationService", new StringWriter(), ring);

		logger.LogInformation("<<< an event");
		logger.LogWarning("[stage 12:00:00.000] replication: online");

		var lines = ring.ReadLines().ToArray();
		await Assert.That(lines.Length).IsEqualTo(1);
		await Assert.That(lines[0]).EndsWith("[WRN] [S.EventReplicationService] [stage 12:00:00.000] replication: online");
	}

	class MemoryLogStorage : IEmergencyLogStorage
	{
		public Dictionary<string, string> Items { get; } = new Dictionary<string, string>();

		public int Writes { get; private set; }

		/// <summary>When set, every write throws the storage's refusal with this as its cause.</summary>
		public Exception? Refusal { get; set; }

		public string? GetItem(string key)
			=> Items.TryGetValue(key, out var value) ? value : null;

		public void SetItem(string key, string value)
		{
			if (Refusal != null)
			{
				throw new EmergencyLogStorageException($"refused to write {key}", Refusal);
			}
			Writes++;
			Items[key] = value;
		}
	}
}
