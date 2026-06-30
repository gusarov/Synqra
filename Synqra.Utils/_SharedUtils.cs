namespace Synqra;

static class AsyncInvoker
{
	public static async Task InvokeAsync(Task task)
	{
		try
		{
			await task;
		}
		catch (Exception ex)
		{
			_ = Task.Run(() => Console.Error.WriteLine($"AsyncInvoker: {ex}"));
			throw;
		}
	}
}