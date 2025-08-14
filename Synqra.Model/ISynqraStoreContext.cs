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

/*
internal static class SynqraStoreContextInternalExtensions
{
	internal static Guid GetId(this ISynqraStoreContext ctx, object model, GetIdMode mode)
	{
		return ((ISynqraStoreContextInternal)ctx).GetId(model, mode);
	}

	internal static (bool IsJustCreated, Guid Id) GetOrCreateId(this ISynqraStoreContext ctx, object model)
	{
		return ((ISynqraStoreContextInternal)ctx).GetOrCreateId(model);
	}
}

internal interface ISynqraStoreContextInternal
{
	Guid GetId(object model, GetIdMode mode);
	(bool IsJustCreated, Guid Id) GetOrCreateId(object model);
}
*/
