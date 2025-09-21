using System.Diagnostics;

namespace Synqra;

/// <summary>
/// This logger can be used in any circumstance where normal logging is not available. E.g. in source generators.
/// No threads, no async/await, no complex dependencies, no state, no buffers.
/// Every call synchronized system-wide with a named Mutex.
/// Every call opens a handle to append to a log file in the temp folder.
/// No flushing, no caching, no batching.
/// It is not performance efficient but reliable and always ready.
/// </summary>
public class SynqraEmergencyLog
{
	public static SynqraEmergencyLog Default { get; } = new SynqraEmergencyLog();

	private SynqraEmergencyLog()
	{
		_logFilePath = Path.Combine(Path.GetTempPath(), "SynqraEmergency.log");
	}

	private string _logFilePath;

	[Conditional("DEBUG")]
	public void Debug(string message)
	{
		LogMessage($"[Debug] {message}");
	}

	public void LogMessage(string message)
	{
		// TODO use \\global and ACL on windows (or don't... TMP is session specific anyway)
		// TODO do something for browser
		using var mutex = new Mutex(false, "SynqraEmergencyLog");
		try
		{
			mutex.WaitOne();
			var fi = new FileInfo(_logFilePath);
			if (fi.Exists && fi.Length > 1024 * 1024)
			{
				fi.Delete();
				LogMessage("[System] Previous log file exceeded 1MiB and deleted");
			}
			File.AppendAllText(_logFilePath, $"[{DateTime.UtcNow:o}] {message}{Environment.NewLine}");
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}
}
