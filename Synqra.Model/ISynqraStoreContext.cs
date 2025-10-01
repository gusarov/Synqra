namespace Synqra;

public interface ISynqraStoreContext : ICommandVisitor<CommandHandlerContext>, IEventVisitor<EventVisitorContext>
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

	/*
	[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	[System.Obsolete("Not part of this API. Use assertion methods instead.", true)]
	public new bool Equals(object? obj) => throw new NotSupportedException();

	[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	[System.Obsolete("Not part of this API. Use assertion methods instead.", true)]
	public new int GetHashCode() => throw new NotSupportedException();

	[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	[System.Obsolete("Not part of this API. Use assertion methods instead.", true)]
	public new string? ToString() => throw new NotSupportedException();
	*/
}
