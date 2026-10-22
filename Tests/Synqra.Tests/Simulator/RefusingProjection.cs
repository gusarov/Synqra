#if NET10_0_OR_GREATER
using System.Threading.Tasks;

namespace Synqra.Tests.Simulator;

/// <summary>
/// A projection that refuses every apply, as the Mongo projection does for an event whose state is already there
/// (a facet re-added to a unique slot). A master whose log already holds an event must never reach it.
/// </summary>
internal sealed class RefusingProjection : IProjection
{
	public int Applied { get; private set; }

	Task Refuse(Event ev)
	{
		Applied++;
		throw new InvalidOperationException($"{ev.GetType().Name} {ev.EventId} was applied although the log holds it already.");
	}

	// IEventVisitor<EventVisitorContext>
	public Task BeforeVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx) => Refuse(ev);
	public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx) => Refuse(ev);
	public Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx) => Refuse(ev);
	public Task VisitAsync(ComponentAddedEvent ev, EventVisitorContext ctx) => Refuse(ev);
	public Task VisitAsync(ComponentPropertyChangedEvent ev, EventVisitorContext ctx) => Refuse(ev);
	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx) => Refuse(ev);

	// ICommandVisitor<CommandHandlerContext>
	public Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(DeleteObjectCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ChangeObjectPropertyCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(AddComponentCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ChangeComponentPropertyCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(DeleteComponentCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(BatchCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
}
#endif
