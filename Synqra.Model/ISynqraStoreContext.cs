namespace Synqra;

public interface ISynqraStoreContext
{
	IStoreCollection Get(Type type);

	IStoreCollection<T> Get<T>()
		where T : class
#if NET8_0_OR_GREATER
	{
		return (IStoreCollection<T>)Get(typeof(T));
	}
#else
	;
#endif

	/// <summary>
	/// Submit command both locally and to the other participants.
	/// </summary>
	Task SubmitCommandAsync(ISynqraCommand newCommand);
}
