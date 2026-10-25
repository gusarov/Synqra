using System.Globalization;
using System.IO;

namespace Synqra;

/// <summary>A synchronous key-value store the browser's EmergencyLog keeps its last lines in.</summary>
internal interface IEmergencyLogStorage
{
	/// <summary>The value under the key, or null when there is none.</summary>
	string? GetItem(string key);

	void SetItem(string key, string value);
}

/// <summary>The storage refused a read or a write: its quota is full, or it is disabled.</summary>
internal sealed class EmergencyLogStorageException : Exception
{
	public EmergencyLogStorageException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}
}

/// <summary>
/// The browser's last EmergencyLog lines, kept in localStorage so a frozen tab's trace can be read from another tab or after a reload.
/// One key per line, {prefix}0 .. {prefix}{capacity - 1}, and {prefix}head with the next sequence number: a line costs one small read
/// and two small writes, never a rewrite of a big key. A line is "{sequence} {UTC time} {tab} {text}"; tabs share the ring and its head.
/// </summary>
internal sealed class EmergencyLogRing
{
	private readonly IEmergencyLogStorage _storage;
	private readonly TextWriter _console;
	private readonly string _prefix;
	private readonly int _capacity;
	private readonly int _maxTextLength;
	private bool _failureReported;

	public EmergencyLogRing(IEmergencyLogStorage storage, TextWriter console, string prefix = "synqra.log.", int capacity = 500, int maxTextLength = 4096)
	{
		_storage = storage;
		_console = console;
		_prefix = prefix;
		_capacity = capacity;
		_maxTextLength = maxTextLength;
	}

	/// <summary>This tab's id in its lines, so the lines of a frozen tab stand apart from the tab reading them.</summary>
	public string Tab { get; } = Guid.NewGuid().ToString("N").Substring(0, 6);

	private string HeadKey => _prefix + "head";

	public void Append(string text)
	{
		try
		{
			var head = ReadHead();
			if (text.Length > _maxTextLength)
			{
				text = text.Substring(0, _maxTextLength) + "…";
			}
			_storage.SetItem(_prefix + (head % _capacity).ToString(CultureInfo.InvariantCulture), $"{head.ToString(CultureInfo.InvariantCulture)} {DateTime.UtcNow:yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'} {Tab} {text}");
			_storage.SetItem(HeadKey, (head + 1).ToString(CultureInfo.InvariantCulture));
		}
		catch (EmergencyLogStorageException ex)
		{
			// Tolerable: the console still gets every line. Reported once, so a full or disabled storage does not repeat it per line.
			if (!_failureReported)
			{
				_failureReported = true;
				_console.WriteLine($"SynqraEmergencyLog [ERR] [EmergencyLogRing] localStorage refused a line, the ring misses lines while it does: {ex}");
			}
		}
	}

	/// <summary>The ring's lines, oldest first.</summary>
	public IEnumerable<string> ReadLines()
	{
		var lines = new List<KeyValuePair<long, string>>();
		for (var i = 0; i < _capacity; i++)
		{
			var line = _storage.GetItem(_prefix + i.ToString(CultureInfo.InvariantCulture));
			if (line != null)
			{
				var space = line.IndexOf(' ');
				if (space > 0 && long.TryParse(line.Substring(0, space), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
				{
					lines.Add(new KeyValuePair<long, string>(sequence, line));
				}
			}
		}
		return lines.OrderBy(x => x.Key).Select(x => x.Value);
	}

	/// <summary>The next sequence number, read every time: another tab may have written since.</summary>
	private long ReadHead()
	{
		var text = _storage.GetItem(HeadKey);
		return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var head) ? head : 0;
	}
}
