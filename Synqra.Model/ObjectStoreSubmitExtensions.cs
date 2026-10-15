using Microsoft.Extensions.Logging;

namespace Synqra;

public static class ObjectStoreSubmitExtensions
{
	/// <summary>
	/// Submits and waits: the one synchronous write path (collection Add, generated setters, links). Off the
	/// browser the wait is a real block made under <see cref="NoSynchronizationContext"/>; on the browser
	/// nothing can block, so the submit runs on and a failure is logged.
	/// </summary>
	public static void SubmitCommand(this IObjectStore store, ISynqraCommand command, CommandSubmissionOptions? options = null)
	{
		if (OperatingSystem.IsBrowser())
		{
			_ = store.SubmitCommandAsync(command, options).LogFailureAsync();
		}
		else
		{
			using var _ = new NoSynchronizationContext();
			store.SubmitCommandAsync(command, options).GetAwaiter().GetResult();
		}
	}

	/// <summary>Observes a task nobody can wait for: a failure is logged, never lost.</summary>
	public static async Task LogFailureAsync(this Task task)
	{
		try
		{
			await task;
		}
		catch (Exception ex)
		{
			EmergencyLog.Default.LogError(ex, "A submit nobody could wait for failed"); // tolerable: the caller has moved on, the failure is on record
		}
	}
}
