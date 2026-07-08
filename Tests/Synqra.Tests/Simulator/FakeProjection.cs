#if NET10_0_OR_GREATER
using System.Threading.Tasks;

namespace Synqra.Tests.Simulator;

/// <summary>
/// A projection that does nothing, in the style of <see cref="FakeAppendStorage"/>. The real WS
/// replication endpoint resolves an <see cref="IProjection"/> from DI and calls Accept on every
/// inbound event before it appends and broadcasts; the stream-isolation e2e test only cares about
/// who receives which event over the socket, not about projected state, so the master host registers
/// this instead of a real stream-aware projection. Every method is a completed no-op.
/// </summary>
internal sealed class FakeProjection : IProjection
{
	// IEventVisitor<EventVisitorContext>
	public Task BeforeVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Event ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ObjectCreatedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ObjectPropertyChangedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ObjectDeletedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(CommandCreatedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ComponentAddedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ComponentPropertyChangedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ComponentDeletedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(LinkAddedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;
	public Task VisitAsync(LinkRemovedEvent ev, EventVisitorContext ctx) => Task.CompletedTask;

	// ICommandVisitor<CommandHandlerContext>
	public Task BeforeVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task AfterVisitAsync(Command cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(CreateObjectCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(DeleteObjectCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ChangeObjectPropertyCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(AddComponentCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(ChangeComponentPropertyCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(DeleteComponentCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(AddLinkCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
	public Task VisitAsync(RemoveLinkCommand cmd, CommandHandlerContext ctx) => Task.CompletedTask;
}
#endif
