using Synqra.AppendStorage;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra.Tests.Contoso;

public class FooContosoCommand : Command
{
	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
	{
		return ((IContosoCommandVisitor<T>)visitor).VisitAsync(this, ctx);
	}
}

public class FooContosoEvent : Event
{
	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
	{
		return ((IContosoEventVisitor<T>)visitor).VisitAsync(this, ctx);
	}
}

public interface IContosoCommandVisitor<T> : ICommandVisitor<T>
{
	Task VisitAsync(FooContosoCommand command, T ctx);
}

public interface IContosoEventVisitor<T> : IEventVisitor<T>
{
	Task VisitAsync(FooContosoEvent command, T ctx);
}

public class ContosoInMemoryProjection : InMemoryProjection, IContosoCommandVisitor<CommandHandlerContext>, IContosoEventVisitor<EventVisitorContext>
{
	public ContosoInMemoryProjection(
		  IAppendStorage<Event, Guid> eventStorage
		, IEventReplicationService? eventReplicationService = null
		, JsonSerializerOptions? jsonSerializerOptions = null
		, JsonSerializerContext? jsonSerializerContext = null
		) : base(
			  eventStorage
			, eventReplicationService
			, jsonSerializerOptions
			, jsonSerializerContext
			)
	{
	}

	public Task VisitAsync(FooContosoCommand command, CommandHandlerContext ctx)
	{
		throw new NotImplementedException();
	}

	public Task VisitAsync(FooContosoEvent command, EventVisitorContext ctx)
	{
		throw new NotImplementedException();
	}
}
