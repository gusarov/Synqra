namespace Synqra;

internal class CodeGenUtils
{
	public static CodeGenUtils Default { get; } = new CodeGenUtils();

	private CodeGenUtils()
	{
	}

	public void WriteFile(string synqraBuildBoxId, string filePath, string originalContent, string newContent)
	{
		if (string.IsNullOrEmpty(filePath))
		{
			EmergencyLog.Default.Error("[CodeGenUtils] Write: There is no filePath specified!");
			return;
		}
		if (!File.Exists(filePath))
		{
			EmergencyLog.Default.Error($"[CodeGenUtils] Write: The file {filePath} does not exists!");
			return;
		}
		using (var mutex = new Mutex(false, "Synqra_CodeGenUtils_Write"))
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
				var current = File.ReadAllText(filePath);
				if (current == originalContent)
				{
					EmergencyLog.Default.Message("WriteFile: " + filePath);
					File.WriteAllText(filePath, newContent);
				}
				else if (current != newContent)
				{
					EmergencyLog.Default.Debug("WriteFile: File has changed since last read, skip writing file: " + filePath);
				}
			}
			finally
			{
				if (acquired)
				{
					mutex.ReleaseMutex();
				}
			}
		}
		// EmergencyLog.Default.Message("WriteFile: " + filePath);
		// File.WriteAllText(filePath, content);
		return;
		/*
		if (string.IsNullOrEmpty(synqraBuildBoxId))
		{
			EmergencyLog.Default.Error("WriteFile: synqraBuildBoxId is not set, skip writing file: " + filePath);
			return;
		}
		var path = Path.Combine(Path.GetTempPath(), "Synqra", "Build", synqraBuildBoxId);
		Directory.CreateDirectory(path);
		var dirs = new DirectoryInfo(path).GetDirectories();
		foreach (var dir in dirs)
		{
			if ((DateTime.UtcNow - dir.LastWriteTimeUtc).TotalMinutes > 10)
			{
				EmergencyLog.Default.Message("Deleting old box: " + dir.FullName);
				dir.Delete(true);
			}
		}
		path = Path.Combine(path, synqraBuildBoxId);
		Directory.CreateDirectory(path);
		path = Path.Combine(path, Path.GetFileName(filePath));
		EmergencyLog.Default.Message("WriteFile: " + path);
		File.WriteAllText(path, content);
		ThreadPool.QueueUserWorkItem(delegate
		{
			Thread.Sleep(5000);
			try
			{
				EmergencyLog.Default.Debug("WriteFile FromThread: " + filePath);
				File.WriteAllText(filePath + "_", content);
				File.WriteAllText(filePath, content);
				EmergencyLog.Default.Debug("WriteFile FromThread Done: " + filePath);
			}
			catch (Exception ex)
			{
				EmergencyLog.Default.Error("WriteFile FromThread Failed", ex);
			}
		});
		*/
	}

	/*
	public string ReadFile(string filePath)
	{
		return File.ReadAllText(filePath);
	}
	*/
}
