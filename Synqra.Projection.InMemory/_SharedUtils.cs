namespace Synqra;

static class AsyncInvoker
{
	public static async void InvokeAsync(Task task)
	{
		try
		{
			await task;
		}
		catch (Exception ex)
		{
			_ = Task.Run(async () =>
			{
				Console.Error.WriteLine($"AsyncInvoker: {ex}");
			});
		}
	}
}