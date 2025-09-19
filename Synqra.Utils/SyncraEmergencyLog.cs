namespace Synqra;

/// <summary>
/// This logger can be used in any circumstance where normal logging is not available. E.g. in source generators.
/// </summary>
public class SyncraEmergencyLog
{
	public SyncraEmergencyLog Default { get; } = new SyncraEmergencyLog();

	private SyncraEmergencyLog()
	{
		
	}

	private readonly List<string> _logMessages = new List<string>();
	private string? _logFilePath = null;
	private readonly object _lock = new();

	private string _logInitMessage = "[+] Generated Log File this file contains log messages from the source generator\n\n";
	private LoggingLevel _loggingLevel = LoggingLevel.Info;

	public void SetLoggingLevel(LoggingLevel level)
	{
		_loggingLevel = level;
	}

	public void SetLogFilePath(string path)
	{
		_logFilePath = path;
		if (File.Exists(_logFilePath))
		{
			File.Delete(_logFilePath);
		}
	}

	public LoggingLevel GetLoggingLevel()
	{
		return _loggingLevel;
	}

	public void LogMessage(string message, LoggingLevel messageLogLevel = LoggingLevel.Info)
	{
		lock (_lock)
		{
			try
			{
				if (_logFilePath is null)
				{
					return;
				}
				if (File.Exists(_logFilePath) is false)
				{
					File.WriteAllText(_logFilePath, _logInitMessage);
					File.AppendAllText(_logFilePath, $"Logging started at {GetDateTimeUtc()}\n\n");
				}
				if (messageLogLevel < _loggingLevel)
				{
					return;
				}
				string _logMessage = message + "\n";
				if (messageLogLevel > LoggingLevel.Info)
				{
					_logMessage = $"[{messageLogLevel} start]\n" + _logMessage + $"[{messageLogLevel} end]\n\n";
				}
				if (!_logMessages.Contains(_logMessage))
				{
					File.AppendAllText(_logFilePath, _logMessage);
					_logMessages.Add(_logMessage);
				}
			}
			catch (Exception ex)
			{
				if (_logFilePath is null)
				{
					return;
				}
				File.AppendAllText(_logFilePath, $"[-] Exception occurred in emergency logging: {ex.Message} \n");
			}
		}
	}

	public void EndLogging()
	{
		if (_logFilePath is null)
		{
			return;
		}
		if (File.Exists(_logFilePath))
		{
			File.AppendAllText(_logFilePath, $"[+] Logging ended at {GetDateTimeUtc()}\n");
		}
	}

	static string GetDateTimeUtc()
	{
		return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
	}
}

public enum LoggingLevel
{
	Trace,
	Debug,
	Info,
	Warning,
	Error,
	Fatal
}
