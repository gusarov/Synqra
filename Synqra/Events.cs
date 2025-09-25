using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra;

public abstract class SingleObjectEvent : Event
{
	public required Guid TargetId { get; init; } // like row id
	public required Guid TargetTypeId { get; init; } // like descriminator
	public required Guid CollectionId { get; init; } // like table name (can be derrived from root type id)
}

public abstract class Event
{
	public required Guid EventId { get; init; }
	public required Guid CommandId { get; set; }
	// public required Guid UserId { get; set; }
	public required Guid ContainerId { get; set; } // like layer id

	public async Task AcceptAsync(IEventVisitor<object?> visitor)
	{
		await visitor.BeforeVisitAsync(this, null);
		await AcceptCoreAsync(visitor, null);
		await visitor.AfterVisitAsync(this, null);
	}

	public async Task AcceptAsync<T>(IEventVisitor<T> visitor, T ctx)
	{
		await visitor.BeforeVisitAsync(this, ctx);
		await AcceptCoreAsync(visitor, ctx);
		await visitor.AfterVisitAsync(this, ctx);
	}

	protected abstract Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx);
}

public interface IEventVisitor<in T>
{
	Task BeforeVisitAsync(Event ev, T ctx);
	Task AfterVisitAsync(Event ev, T ctx);

	Task VisitAsync(ObjectCreatedEvent ev, T ctx);
	Task VisitAsync(ObjectPropertyChangedEvent ev, T ctx);
	Task VisitAsync(ObjectDeletedEvent ev, T ctx);
	Task VisitAsync(CommandCreatedEvent ev, T ctx);

	/*
	Task VisitAsync(CommandCreated ev, T ctx);
	Task VisitAsync(CommandPatched ev, T ctx);

	Task VisitAsync(ComponentAdded ev, T ctx);
	Task VisitAsync(ComponentPropertyChanged ev, T ctx);
	Task VisitAsync(ComponentDeleted ev, T ctx);

	Task VisitAsync(WorldStateProvided ev, T ctx);

	Task VisitAsync(NodeMoved ev, T ctx);
	Task VisitAsync(DependencyChanged ev, T ctx);
	Task VisitAsync(SettingChanged ev, T ctx);
	*/
}

public class ObjectCreatedEvent : SingleObjectEvent
{
	public IDictionary<string, object?>? Data { get; set; }

	[JsonIgnore]
	public string? DataString { get; set; }

	[JsonIgnore]
	public object? DataObject { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}

public class ObjectPropertyChangedEvent : SingleObjectEvent
{
	public required string PropertyName { get; init; }
	public object? OldValue { get; set; }
	public object? NewValue { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}

public class ObjectDeletedEvent : SingleObjectEvent
{
	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}

public class CommandCreatedEvent : Event
{
	public required Command Data { get; init; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
