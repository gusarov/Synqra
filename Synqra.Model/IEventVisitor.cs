namespace Synqra;

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
