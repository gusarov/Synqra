using System.Diagnostics.CodeAnalysis;

namespace Synqra;

public interface IObjectStore
{
	/// <summary>
	/// Get Id of the model instance. If model is not tracked, throw exception.
	/// </summary>
	/// <param name="model"></param>
	/// <returns></returns>
	Guid GetId(object model);
	ISynqraCollection GetCollection(Type type);

	// [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
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

public interface IProjection : ICommandVisitor<CommandHandlerContext>, IEventVisitor<EventVisitorContext>
{
}
