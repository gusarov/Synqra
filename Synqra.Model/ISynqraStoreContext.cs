namespace Synqra;

public interface ISynqraStoreContext
{
	ISynqraCollection Get(Type type);

	ISynqraCollection<T> Get<T>()
		where T : class
#if NET8_0_OR_GREATER
	{
		return (ISynqraCollection<T>)Get(typeof(T));
	}
#else
	;
#endif

	/// <summary>
	/// Submit command both locally and to the other participants.
	/// </summary>
	Task SubmitCommandAsync(ISynqraCommand newCommand);
}
