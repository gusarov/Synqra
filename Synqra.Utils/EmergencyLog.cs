using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Synqra;

/// <summary>
/// This logger can be used in any circumstance where normal logging is not available. E.g. in source generators.
/// No threads, no async/await, no complex dependencies, no state, no buffers.
/// Every call synchronized system-wide with a named Mutex.
/// Every call opens a handle to append to a log file in the temp folder.
/// No flushing, no caching, no batching.
/// It is not performance efficient but reliable and always ready.
/// It makes 1MiB file size limit and rolls over between multiple files.
/// It keeps a logs for 1 week.
/// It holds 100 files maximum (100MiB total) but expected to cleanup older files sooner.
/// The encoding is utf8 (no_bom)
/// </summary>
public class EmergencyLog
{
	public static EmergencyLog Default { get; } = new EmergencyLog();

	private EmergencyLog()
	{
		if (!AppContext.TryGetSwitch("Synqra.EmergencyLog.Enabled", out var isEnabled))
		{
			isEnabled = true;
		}
		if (isEnabled)
		{
			var dir = Path.Combine(Path.GetTempPath(), "Synqra");
			Directory.CreateDirectory(dir);
			_logFilePath = Path.Combine(dir, "Emergency.log");
			_logFilePathTemplate = Path.Combine(dir, "Emergency_{0}.log");
			var mutexName = _logFilePath.ToUpperInvariant();
			foreach (var c in Path.GetInvalidFileNameChars())
			{
				mutexName = mutexName.Replace(c, '_');
			}
			// mutex name uniquiness should corelate with temp folder uniquiness.
			// When TMP configured per-user, or per-session, or per-machine same would ne with mutex name
			_mutexName = $"Global\\SynqraEmergencyLog_{mutexName}";
			// _mutexName = $"Global\\SynqraEmergencyLog";
		}
	}

	private string? _logFilePath;
	private string? _mutexName;
	private string? _logFilePathTemplate;

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
		try
		{
			if (_logFilePath == null || _logFilePathTemplate == null)
			{
				return;
			}
			// TODO use \\global and ACL on windows (or don't... TMP is session specific anyway. UPD: No, I just set both system and user tmp to C:\Dev\Temp and now I have services vs user going to same file. Mutext needs to be global on Windows)
			// TODO do something for browser
			message = $"[{DateTimeOffset.Now:yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'fffffffzzz}] {message}{Environment.NewLine}";

			Mutex mutex = null;
#if NET8_0_OR_GREATER
			if (OperatingSystem.IsWindows())
			{
				try
				{
					var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
					var security = new MutexSecurity();
					security.AddAccessRule(new MutexAccessRule(everyone, MutexRights.Modify | MutexRights.Synchronize, AccessControlType.Allow));//| MutexRights.ReadPermissions | MutexRights.TakeOwnership
					var self = WindowsIdentity.GetCurrent().User;
					if (self != null)
					{
						security.AddAccessRule(new MutexAccessRule(self, MutexRights.FullControl, AccessControlType.Allow));
					}

					bool createdNew;
					mutex = MutexAcl.Create(
						initiallyOwned: false,
						name: _mutexName,
						createdNew: out createdNew,
						mutexSecurity: security);
				}
				catch
				{
					// It will fall back to general approach below. Unfortunately, can't log this here to avoid loops.
#if DEBUG
					// WARNING! If you got exception and break here - take a good care about the cause, think it thoroughly, there are zero exceptions expected.
					Debugger.Break();
					throw;
#endif
				}
			}
			else if (OperatingSystem.IsBrowser())
			{
				Console.WriteLine("⚠️🚨 SynqraEmergencyLog: " + message);
				return;
			}
#endif
			if (mutex == null)
			{
				mutex = new Mutex(false, _mutexName);
			}
			using (mutex)
			{
				bool acquired = false;
				try
				{
					acquired = mutex.WaitOne();
				}
				catch (AbandonedMutexException)
				{
					// The mutex was abandoned in another process, it will still get acquired. No need to do anything special here, it is normal case.
					acquired = true;
				}
				try
				{
					var fi = new FileInfo(_logFilePath);
					if (fi.Exists && fi.Length > 1024 * 1024)
					{
						try
						{
							/*
							 Here we go - the rollover logic!
							 Honestly it does not feel fair that EmergencyLog is not as reliable as it claims. It should be as a black box for
							 flight crash research. Chances are, people log things that can happen once a month mysteriously and hard to reproduce...
							 And what you will say? File is too big and have to be deleted?
							 2025-10-05 I decided to change my mind and implement a simple rollover between 2 files instead of deleting the log file.
							*/

							/* V1 [Problem: can lose some data randomly because of roll over]
							fi.Delete();
							Message("[System] Previous log file exceeded 1MiB and deleted");
							*/

							/* V2 1 bak file [Problem: can lose some data if there are a lot of rapid events. Need a time-based fix]
							var fi2 = new FileInfo(_logFilePath2);
							if (fi2.Exists)
							{
								fi2.Delete();
							}
							fi.MoveTo(_logFilePath2);
							*/

							// V3 Log4Net style rename chain
							var now = DateTime.UtcNow;
							float maxFile = 0;
							const int MaxTotalFiles = 100;
							string DealWithNextFile(int number)
							{
								maxFile = number;
								var file = string.Format(_logFilePathTemplate, number);
								var fi = new FileInfo(file);
								if (fi.Exists)
								{
									bool needToDrop = (now - fi.LastWriteTimeUtc).TotalDays > 7 || number > MaxTotalFiles; // this must have at least some limit, and 100 files looks like way too much. Having said that it is just 100MB (1MB per file), so it could make sense to increase 1 file size.
									var next = DealWithNextFile(number + 1);
									if (needToDrop)
									{
										fi.Delete();
									}
									else
									{
										fi.MoveTo(next);
									}
								}
								return file;
							}
							fi.MoveTo(DealWithNextFile(2));
							Message($"[EmergencyLog] Previous log file exceeded 1MiB and renamed. It is {maxFile} files a week ({maxFile / MaxTotalFiles:P0} capacity)");
						}
						catch
						{
#if DEBUG
							Debugger.Break();
							throw;
#endif
						}
					}
					FileAppendAllText(_logFilePath, message);
				}
				finally
				{
					if (acquired)
					{
						mutex.ReleaseMutex();
					}
				}
			} // Dispose() happens here, after ReleaseMutex()
		}
		catch
		{
			// Guaranteed no impact on the main application
#if DEBUG
			// WARNING! If you got exception and break here - take a good care about the cause, think it thoroughly, there are zero exceptions expected.
			Debugger.Break();
			throw;
#endif
		}
	}

	private void FileAppendAllText(string fileName, string message)
	{
		using var file = File.Open(fileName, FileMode.Append, FileAccess.Write, FileShare.Read);
		using var sw = new StreamWriter(file);
		/*
		using var sw2 = new StreamWriter(fileName, new FileStreamOptions
		{
			Access = FileAccess.Write,
			Mode = FileMode.Append,
			PreallocationSize = 1024 * 1024,
			Share = FileShare.Read,
			// Options = FileOptions.SequentialScan,
			// BufferSize = 1024 * 1024,
		});
		*/
		sw.Write(message);
	}
}
