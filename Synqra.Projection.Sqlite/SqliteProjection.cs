using Synqra.AppendStorage;

namespace Synqra.Projection.Sqlite;

using IAppendStorage = IAppendStorage<Event, Guid>;

public class SqliteProjection : IProjection
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
