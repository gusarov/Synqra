using Synqra;

namespace Contoso.WebHost;

/// <summary>
/// The Contoso master keeps no read model of its own: its event log is the truth it replays to every browser. The replication
/// endpoint applies each inbound event to the registered projection before appending it; here that apply does nothing.
/// </summary>
public sealed class ContosoLogOnlyProjection : IProjection
{
	public Task BeforeVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ComponentAddedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ComponentPropertyChangedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;

	public Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(DeleteObjectCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ChangeObjectPropertyCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(AddComponentCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ChangeComponentPropertyCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(DeleteComponentCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(BatchCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
}
