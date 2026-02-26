using Synqra.AppendStorage;

namespace Synqra.Projection.Sqlite;

using IAppendStorage = IAppendStorage<Event, Guid>;

internal static class SynqraStoreContextInternalExtensions
{
	/*
	internal static Guid GetId(this IObjectStore ctx, object model, ISynqraCollection? collection, GetMode mode)
	{
		return ctx.GetId(model, collection, mode);
	}

	internal static AttachedObjectData Attach(this IObjectStore ctx, object model, ISynqraCollection collection)
	{
		return ctx.Attach(model, collection);
	}

	internal static (bool IsJustCreated, Guid Id) GetOrCreateId(this IObjectStore ctx, object model, ISynqraCollection collection)
	{
		return ctx.GetOrCreateId(model, collection);
	}
	*/
}

internal class AttachedObjectData
{
	public required Guid Id { get; init; }
	public required ISynqraCollection Collection { get; init; }
	public required bool IsJustCreated { get; set; }
}

// It is not flags, as all possible permutations are defined explicitly
internal enum GetMode : byte
{
	// 0b_0000_0000
	//          MME
	// E - Behavior for existing object (0 - throw, 1 - return)
	// MM - Behavior for missing object (0 - throw, 1 - zero_default, 2 - create_id)

	// 0b_MM_E
	Invalid,     // 00 0
	RequiredId,  // 00 1
	MustAbsent,  // 01 0
	TryGet,      // 01 1
	RequiredNew, // 10 0
	GetOrCreate, // 10 1
}


public class SqliteStore : IObjectStore
{
	public ISynqraCollection GetCollection(Type type)
	{
		throw new NotImplementedException();
	}

	public Guid GetId(object model)
	{
		throw new NotImplementedException();
	}

	public async Task SubmitCommandAsync(ISynqraCommand newCommand)
	{
	}
}

public class SqliteProjection : IProjection
{
	#region Command Visitor

	public async Task AfterVisitAsync(Command cmd, CommandHandlerContext ctx)
	{
	}

	public async Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx)
	{
	}

	public async Task VisitAsync(CreateObjectCommand cmd, CommandHandlerContext ctx)
	{
	}

	public async Task VisitAsync(DeleteObjectCommand cmd, CommandHandlerContext ctx)
	{
	}

	public async Task VisitAsync(ChangeObjectPropertyCommand cmd, CommandHandlerContext ctx)
	{
	}

	#endregion

	#region Event Visitor

	public async Task BeforeVisitAsync(Event ev, EventVisitorContext ctx)
	{
	}

	public async Task AfterVisitAsync(Event ev, EventVisitorContext ctx)
	{
	}

	public async Task VisitAsync(ObjectCreatedEvent ev, EventVisitorContext ctx)
	{
	}

	public async Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx)
	{
	}

	public async Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx)
	{
	}

	public async Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx)
	{
	}

	#endregion
}
