using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Synqra;

/// <summary>
/// This logger can be used in any circumstance where normal logging is not available. E.g. in source generators.
/// No threads, no async/await, no complex dependencies, no state, no buffers.
/// Every call synchronized system-wide with a named Mutex.
/// Every call opens a handle to append to a log file in the temp folder.
/// No flushing, no caching, no batching.
/// It is not performance efficient but reliable and always ready.
/// </summary>
public class EmergencyLog
{
	public static EmergencyLog Default { get; } = new EmergencyLog();

	private EmergencyLog()
	{
		_logFilePath = Path.Combine(Path.GetTempPath(), "SynqraEmergency.log");
		// _logFilePath2 = Path.Combine(Path.GetTempPath(), "SynqraEmergency.bak");
		if (!AppContext.TryGetSwitch("Synqra.EmergencyLog.Enabled", out var isEnabled))
		{
			isEnabled = true;
		}
		if (!isEnabled)
		{
			_logFilePath = string.Empty;
		}
	}

	private string _logFilePath;
	// private string _logFilePath2;

	/// <summary>
	/// Conditional message - #if DEBUG
	/// </summary>
	[Conditional("DEBUG")]
	public void Debug(string message)
	{
		Message($"[Debug] {message}");
	}

	/// <summary>
	/// Conditional message - #if TRACE
	/// </summary>
	[Conditional("TRACE")]
	public void Trace(string message)
	{
		Message($"[Trace] {message}");
	}

	public void Message(string message)
	{
		if (_logFilePath == string.Empty)
		{
			return;
		}
		// TODO use \\global and ACL on windows (or don't... TMP is session specific anyway. UPD: No, I just set both system and user tmp to C:\Dev\Temp and now I have services vs user going to same file. Mutext needs to be global on Windows)
		// TODO do something for browser
		var line = $"[{DateTime.UtcNow:o}] {message}{Environment.NewLine}";

		Mutex mutex = null;
#if NET8_0_OR_GREATER
		if (OperatingSystem.IsWindows())
		{
			var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
			var rule = new MutexAccessRule(everyone, MutexRights.FullControl, AccessControlType.Allow);

			var security = new MutexSecurity();
			security.AddAccessRule(rule);

			bool createdNew;
			mutex = MutexAcl.Create(
				initiallyOwned: false,
				name: "Global\\SynqraEmergencyLog",
				createdNew: out createdNew,
				mutexSecurity: security);

		}
		else if (OperatingSystem.IsBrowser())
		{
			Console.WriteLine("⚠️🚨 SynqraEmergencyLog: " + line);
			return;
		}
#endif
		if (mutex == null)
		{
			mutex = new Mutex(false, "SynqraEmergencyLog");
		}
		using (mutex)
		{
			try
			{
				mutex.WaitOne();
				var fi = new FileInfo(_logFilePath);
				if (fi.Exists && fi.Length > 1024 * 1024)
				{
					fi.Delete();
					/*
					Message("[System] Previous log file exceeded 1MiB and deleted");
					var fi2 = new FileInfo(_logFilePath2);
					if (fi2.Exists)
					{
						fi2.Delete();
					}
					fi.MoveTo(_logFilePath2);
					*/
				}
				File.AppendAllText(_logFilePath, line);
			}
			finally
			{
				mutex.ReleaseMutex();
			}
		}
	}
}
