using System.Diagnostics.CodeAnalysis;

namespace Synqra;

public interface IObjectStore
{
	ITypeMetadataProvider TypeMetadataProvider { get; }


	/// <summary>
	/// Get Id of the model instance. If model is not tracked, throw exception.
	/// </summary>
	/// <param name="model"></param>
	/// <returns></returns>
	Guid GetId<T>(T model) where T : class // this is optional to implement, for potentially higher performance
#if NET8_0_OR_GREATER
		=> GetId((object)model)
#endif
		;

	/// <summary>
	/// Get Id of the model instance. If model is not tracked, throw exception.
	/// </summary>
	/// <param name="model"></param>
	/// <returns></returns>
	Guid GetId(object model); // this is required to implement for unlimited capabilities

	ISynqraCollection GetCollection(Type type, string? collectionName = null); // this is required to implement for unlimited capabilities

	// [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
	ISynqraCollection<T> GetCollection<T>(string? collectionName = null) // this is optional to implement, for potentially higher performance
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

public interface ITypeMetadataProvider
{
	TypeMetadata GetTypeMetadata(Guid typeId);
	TypeMetadata GetTypeMetadata(Type type);
	void RegisterType(Type type);
}

public class TypeMetadata
{
	static TypeMetadata()
	{
		AppContext.SetSwitch("Synqra.GuidExtensions.ValidateNamespaceIdHashChain", false); // we use v5 type id as a next namespace for collection id
	}

	public Type Type { get; set; }
	public Guid TypeId { get; set; }

	private Guid _defaultCollectionId;

	public Guid GetCollectionId(string name)
	{
		if ((name ?? throw new ArgumentNullException(nameof(name))) == "")
		{
			if (_defaultCollectionId == default)
			{
				_defaultCollectionId = GetCollectionIdInternal(name);
			}
			return _defaultCollectionId;
		}
		return GetCollectionIdInternal(name);
	}

	Guid GetCollectionIdInternal(string name)
	{
		return GuidExtensions.CreateVersion5(TypeId, $"col_{name}"); // col denotes that we convert type id to collection id. In case we would need more id types, we have a prefix to distinguish them
	}

	public override string ToString()
	{
		return $"{TypeId.ToString("N")[..4]} {Type.Name}";
	}
}

public interface IProjection : ICommandVisitor<CommandHandlerContext>, IEventVisitor<EventVisitorContext>
{
}
