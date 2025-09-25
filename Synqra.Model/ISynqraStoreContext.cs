namespace Synqra;

public interface ISynqraStoreContext
{
	ISynqraCollection GetCollection(Type type);

	ISynqraCollection<T> GetCollection<T>()
		where T : class
#if NET8_0_OR_GREATER
	{
		return (ISynqraCollection<T>)GetCollection(typeof(T));
	}
#else
	;
#endif

	/// <summary>
	/// Submit command both locally and to the other participants.
	/// </summary>
	Task SubmitCommandAsync(ISynqraCommand newCommand);
}
