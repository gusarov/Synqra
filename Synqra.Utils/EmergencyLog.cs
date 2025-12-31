using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Synqra;

public sealed class EmergencyLog : ILogger
{
	public static EmergencyLog Default { get; } = new EmergencyLog(EmergencyLoggerProvider.DefaultLogger);

	public string LogPath => EmergencyLogImplementation.Default.LogPath;

	EmergencyLog(ILogger logger)
	{
		Logger = logger;
	}

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		=> Logger.BeginScope(state);

	public bool IsEnabled(LogLevel logLevel)
		=> Logger.IsEnabled(logLevel);

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		=> Logger.Log(logLevel, eventId, state, exception, formatter);

	internal ILogger Logger { get; }
}

/// <summary>
/// This logger can be used in any circumstance where normal logging is not available. E.g. in source generators.
/// No threads, no async/await, no complex dependencies, no state, no buffers.
/// Every call synchronized system-wide with a named Mutex.
/// Every call ~opens a handle~ to append to a log file in the temp folder.
/// ~No flushing,~ no caching, no batching.
/// It is not performance efficient but reliable and always ready.
/// It makes 1MiB file size limit and rolls over between multiple files.
/// It keeps a logs for 1 week.
/// It holds 100 files maximum (100MiB total) but expected to cleanup older files sooner.
/// The encoding is utf8 (no_bom)
/// </summary>
internal class EmergencyLogImplementation
{
	public static EmergencyLogImplementation Default { get; } = new EmergencyLogImplementation();

	static char _rollingAvoidance = '♫';

	private EmergencyLogImplementation()
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
			// When TMP configured per-user, or per-session, or per-machine same would be with mutex name
			_mutexName = $"Global\\SynqraEmergencyLog_{mutexName}";


#if NET8_0_OR_GREATER
			if (OperatingSystem.IsWindows())
			{
				try
				{
					var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
					var security = new MutexSecurity();
					security.AddAccessRule(new MutexAccessRule(everyone, MutexRights.FullControl, AccessControlType.Allow));//| MutexRights.ReadPermissions | MutexRights.TakeOwnership
					bool createdNew;
					_mutex = MutexAcl.Create(
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
#endif
			if (_mutex == null)
			{
				_mutex = new Mutex(false, _mutexName);
			}
		}
	}

	private string? _logFilePath;
	private string? _mutexName;
	private string? _logFilePathTemplate;
	private Mutex _mutex;
	private static Random _random =
#if NET
		Random.Shared
#else
		new Random()
#endif
		;
	static Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

	public string LogPath => _logFilePath ?? "<Disabled>";

	internal void Message(string message)
	{
		try
		{
			if (_logFilePath == null || _logFilePathTemplate == null)
			{
				return;
			}

			message = string.Join("", message.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Select(static x => Format(x))); // multiline prefixing
// REPLACE || TO && WHEN BROWSER TARGET SUPPORT IS ADDED
#if NET8_0_OR_GREATER || BROWSER
			if (OperatingSystem.IsBrowser())
			{
				Console.WriteLine("⚠️🚨 SynqraEmergencyLog: " + message);
				return;
			}
#endif
			bool acquired = false;
			try
			{
				acquired = _mutex.WaitOne();
			}
			catch (AbandonedMutexException)
			{
				// The mutex was abandoned in another process, it will still get acquired. No need to do anything special here, it is normal case.
				acquired = true;
			}
			try
			{
				if (_random.Next(1000) == 0) // stateless, so montecarlo
				{
					var fi = new FileInfo(_logFilePath);

					const int maxSizeBytes = 20 * 1024 * 1024;
					if (fi.Exists && (fi.Length > maxSizeBytes))
					{
						// slide 50% of file inside! UPD: slide till maxSize/2
						try
						{
							using var rs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
#if !NETSTANDARD
							string header;
							using (var sr = new StreamReader(rs, leaveOpen: true))
							{
								header = sr.ReadLine() ?? throw new Exception(); // header line
							}
#endif
							rs.Position = Math.Max(fi.Length / 2, fi.Length - maxSizeBytes / 2);
							while (rs.ReadByte() != '\n') { }
							using var ws = new FileStream(_logFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
							// using var sw = new StreamWriter(ws);
#if !NETSTANDARD
							using (var sw = new StreamWriter(ws, leaveOpen: true))
							{
								const string rollStr = " Roll ";
								var i = header.IndexOf(rollStr);
								int roll = 0;
								if (i > 0)
								{
									var ie = header.IndexOf(':', i);
									if (i >= 0 && ie > 0)
									{
										header = header.Substring(i + rollStr.Length, ie - i - rollStr.Length);
										roll = int.Parse(header);
									}
								}
								roll++;
								sw.Write(Format($"[INF] [EmergencyLogHeader] Roll {roll}: vSynqra: Previous log file exceeded {maxSizeBytes / (1024 * 1024)}MiB, sliding content"));
								sw.Flush();
							}
#endif
							rs.CopyTo(ws);
							ws.SetLength(ws.Position);
						}
						catch (Exception ex)
						{
#if DEBUG
							Debugger.Break();
#endif
							message = Format($"[Error] [EmergencyLog] Can't roll file: " + ex + Environment.NewLine) + message;

						}
					}
				}
				var fileName = _logFilePath;
				Exception? firstEx = null;
				for (byte i = 0; ; i++) // retry locked file with separate name (should be extremely rare and misuse of storage, but EmergencyLogger guarantees are more important)
				{
					try
					{
						FileAppendAllText(fileName, message);
						break;
					}
					catch (IOException ex)
					{
						if (firstEx == null)
						{
							firstEx = ex;
							message = Format($"[Error] [EmergencyLog] Failed to write to {fileName}, will retry with another file: {ex}") + message;
						}
						if (i < 10)
						{
							fileName = string.Format(_logFilePathTemplate, "Locked_" + i);
						}
						else if (i == 10)
						{
							fileName = string.Format(_logFilePathTemplate
#if NET9_0_OR_GREATER
								, Guid.CreateVersion7() // sortable
#else
								, Guid.NewGuid()
#endif
								);
						}
						else
						{
							throw;
						}
					}
				}
			}
			catch
			{
#if DEBUG
				// WARNING! If you got exception and break here - take a good care about the cause, think it thoroughly, there are zero exceptions expected.
				Debugger.Break();
				throw;
#endif
			}
			finally
			{
				if (acquired)
				{
					_mutex.ReleaseMutex();
				}
			}
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

	private void Message_(string message)
	{
		try
		{
			if (_logFilePath == null || _logFilePathTemplate == null)
			{
				return;
			}
			// TODO use \\global and ACL on windows (or don't... TMP is session specific anyway. UPD: No, I just set both system and user tmp to C:\Dev\Temp and now I have services vs user going to same file. Mutext needs to be global on Windows)
			// TODO do something for browser

			bool isRollingAvoidance = message.Length > 0 && message[0] == _rollingAvoidance;
			if (isRollingAvoidance)
			{
				message = message.Substring(1);
			}
			else if (message.Length > 0 && message[message.Length - 1] == _rollingAvoidance)
			{
				isRollingAvoidance = true;
				message = message.Substring(0, message.Length - 1);
			}

			// message = string.Join("", message.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Select(Format)); // multiline prefixing

			Mutex mutex = null;
#if NET8_0_OR_GREATER
			if (OperatingSystem.IsWindows())
			{
				try
				{
					var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
					var security = new MutexSecurity();
					security.AddAccessRule(new MutexAccessRule(everyone, MutexRights.FullControl, AccessControlType.Allow));//| MutexRights.ReadPermissions | MutexRights.TakeOwnership
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
					// VNext - assess total size of logs to total space on disk. Bigger more expensive PCs should allow bigger logs and better problem solving, so use that. This approach helps to break the empirical values for size or amount of files or number of days to keep.
					// FileAppendAllText(_logFilePath, $"fi.LastWriteTime - default(DateTime) {fi.LastWriteTime - default(DateTime)}\r\n");
					// FileAppendAllText(_logFilePath, $"DateTime.Now - default(DateTime){DateTime.Now - default(DateTime)}\r\n");
					// FileAppendAllText(_logFilePath, $"(int)(fi.LastWriteTime - default(DateTime)).TotalDays{(int)(fi.LastWriteTime - default(DateTime)).TotalDays}\r\n");
					// FileAppendAllText(_logFilePath, $"(int)(DateTime.Now - default(DateTime)).TotalDays{(int)(DateTime.Now - default(DateTime)).TotalDays}\r\n");
					if (!isRollingAvoidance && fi.Exists && (fi.Length > 100 * 1024 * 1024 || (int)(fi.LastWriteTime - default(DateTime)).TotalDays < (int)(DateTime.Now - default(DateTime)).TotalDays)) // rotate every day or if file is too big
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
							const int MaxTotalFiles = 10;
							string DealWithNextFile(int number)
							{
								maxFile = number;
								var file = string.Format(_logFilePathTemplate, number);
								var fi = new FileInfo(file);
								if (fi.Exists)
								{
									bool needToDrop = (now - fi.LastWriteTimeUtc).TotalDays > 7 || number > MaxTotalFiles; // this must have at least some limit, and 100 files looks like way too much, so it is reasonable number.
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
							Message($"[EmergencyLog] Previous log file was renamed. It is {maxFile} files a week ({maxFile / MaxTotalFiles:P0} capacity)");
						}
						catch (Exception ex)
						{
#if DEBUG
							Debugger.Break();
#endif
							message = Format($"[Error] [EmergencyLog] Can't roll file: " + ex + Environment.NewLine) + message + _rollingAvoidance;
						}
					}
					var fileName = _logFilePath;
					Exception? firstEx = null;
					for (byte i = 0;; i++) // retry locked file with separate name (should be extremely rare and misuse of storage, but EmergencyLogger guarantees are more important)
					{
						try
						{
							FileAppendAllText(fileName, message);
							break;
						}
						catch (IOException ex)
						{
							if (firstEx == null)
							{
								firstEx = ex;
								message = Format($"[Error] [EmergencyLog] Failed to write to {fileName}, will retry with another file: {ex}");
							}
							if (i < 10)
							{
								fileName = string.Format(_logFilePathTemplate, "Locked_" + i);
							}
							else if (i == 10)
							{
								fileName = string.Format(_logFilePathTemplate
#if NET9_0_OR_GREATER
									, Guid.CreateVersion7() // sortable
#else
									, Guid.NewGuid()
#endif
									);
							}
							else
							{
								throw;
							}
						}
					}
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

	static string _processName = Process.GetCurrentProcess().ProcessName
#if !NETSTANDARD
		+ (OperatingSystem.IsWindows() ? ".exe" : null)
#endif
		+ $":{Process.GetCurrentProcess().Id}"
		;

	private static string Format(string message, DateTimeOffset at = default)
	{
		if (at == default)
		{
			at = DateTimeOffset.Now;
		}
		message = $"[{at:yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'fffffffzzz}] [{_processName}] {message}{Environment.NewLine}"; // {new StackTrace()}{Environment.NewLine}
		return message;
	}

	FileStream? _fileStream;
	StreamWriter? _streamWriter;

	private void FileAppendAllText(string fileName, string message)
	{
		// File.AppendAllText does not share readers, so we do it manually
		var file = _fileStream ?? (_fileStream = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite));
		if (file.Position != file.Length) // seek is expensive
		{
			file.Position = file.Length;
		}
		// file.Position = file.Length;
		// file.Lock(0, 0); // Lock whole file for this stream only, to prevent other writers to corrupt the log. Readers are still allowed.
		var sw = _streamWriter ?? (_streamWriter = new StreamWriter(file));
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
			sw.Flush(); // have to do this while under locked mutex

		// file.Unlock(0, 0);
	}
}

public static class EmergencyLogExtensions
{
	static HexDumpWriter _hexDumpWriter = new HexDumpWriter();

	/// <summary>
	/// Conditional message - #if DEBUG
	/// </summary>
	[Conditional("DEBUG")]
	// [Obsolete("Use ILogger")]
	public static void DebugHexDump(this EmergencyLog log, ReadOnlySpan<byte> data)
	{
		var sb = new StringBuilder();
		_hexDumpWriter.HexDump(data, x => sb.Append(x), x => sb.Append(x));
		log.LogDebug($"[HexDump] {sb}");
	}

	[Conditional("DEBUG")]
	// [Obsolete("Use ILogger")]
	public static void DebugHexDump(this ILogger log, ReadOnlySpan<byte> data)
	{
		var sb = new StringBuilder();
		_hexDumpWriter.HexDump(data, x => sb.Append(x), x => sb.Append(x));
		log.LogDebug($"[HexDump] {sb}");
	}

	/// <summary>
	/// Conditional message - #if DEBUG
	/// </summary>
	[Conditional("DEBUG")]
	[Obsolete("Use ILogger")]
	public static void Debug(this EmergencyLog log, string message)
	{
		log.LogDebug($"{message}");
	}

	/// <summary>
	/// Conditional message - #if TRACE
	/// </summary>
	[Conditional("TRACE")]
	[Obsolete("Use ILogger")]
	public static void Trace(this EmergencyLog log, string message)
	{
		log.LogTrace($"{message}");
	}

	[Obsolete("Use LogInformation instead")]
	public static void Message(this EmergencyLog log, string message)
	{
		log.Logger.LogInformation(message);
	}

	[Obsolete("Use ILogger.LogError")]
	public static void Error(this EmergencyLog log, string message, Exception? ex = null)
	{
		if (ex == null)
		{
			log.LogError($"{message}");
		}
		else
		{
			log.LogError(ex, $"{message}: {ex}");
		}
	}

	public static void AddEmergencyLogger(this IServiceCollection services)
	{
		services.AddSingleton<ILoggerProvider>(EmergencyLoggerProvider.DefaultProvider);
	}
}

internal class EmergencyLoggerProvider : ILoggerProvider
{
	public static EmergencyLoggerProvider DefaultProvider { get; } = new EmergencyLoggerProvider();
	public static EmergencyLogger DefaultLogger { get; } = (EmergencyLogger)DefaultProvider.CreateLogger(string.Empty);

	private ConcurrentDictionary<string, EmergencyLogger> _loggers = new ConcurrentDictionary<string, EmergencyLogger>();

	private EmergencyLoggerProvider()
	{
		
	}

	public ILogger CreateLogger(string categoryName)
	{
		return _loggers.GetOrAdd(categoryName, _ => new EmergencyLogger(categoryName));
	}

	public void Dispose()
	{
	}
}

internal class EmergencyLogger : ILogger
{
	private readonly string _tags;

	public EmergencyLogger(string categoryName)
	{
		if (!string.IsNullOrEmpty(categoryName))
		{
			_tags += $" [{ShortCat(categoryName)}]";
		}
	}

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull
	{
		return null;
	}

	public bool IsEnabled(LogLevel logLevel)
	{
		return true;
	}

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		var msg = formatter(state, exception);
		if (exception != null)
		{
			var exStr = exception.ToString();
			if (!msg.Contains(exStr))
			{
				msg += $": {exStr}";
			}
		}

		var eventIdStr = eventId.Id != 0 ? $" [{eventId.Id}{(eventId.Name != null ? $":{eventId.Name}" : string.Empty)}]" : string.Empty;
		EmergencyLogImplementation.Default.Message($"[{Level(logLevel)}]{_tags}{eventIdStr} {msg}");
	}

	private string ShortCat(string categoryName)
	{
		// Replace the category abbreviation logic with a zero-allocation, high-performance version
		Span<char> catSpan = stackalloc char[categoryName.Length + 10]; // enough for abbreviation + dot + last segment
		int catLen = 0;
		int lastDot = -1;
		for (int i = 0; i < categoryName.Length; i++)
		{
			if (categoryName[i] == '.')
			{
				lastDot = i;
			}
			else if (i == 0 || categoryName[i - 1] == '.')
			{
				catSpan[catLen++] = categoryName[i];
				catSpan[catLen++] = '.';
			}
		}
		if (catLen > 0 && catSpan[catLen - 1] == '.')
			catLen--; // remove trailing dot

		// Append last segment if there is a dot and it's not empty
		if (lastDot + 1 < categoryName.Length)
		{
			// catSpan[catLen++] = '.';
			for (int i = lastDot + 2; i < categoryName.Length; i++)
				catSpan[catLen++] = categoryName[i];
		}
#if NETSTANDARD2_0
		throw new NotSupportedException();
#else
		var res = new string(catSpan[0..catLen]);
		return res;
#endif
		// return new string(catSpan[0..catLen]);
	}

	static string Level(LogLevel level) => level switch
	{
		LogLevel.Trace => "TRC",
		LogLevel.Debug => "DBG",
		LogLevel.Information => "INF",
		LogLevel.Warning => "WRN",
		LogLevel.Error => "ERR",
		LogLevel.Critical => "CRT",
		_ => level.ToString(),
	};
}
