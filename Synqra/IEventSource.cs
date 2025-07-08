namespace Synqra;

public interface IStoreContext
{

}

/// <summary>
/// Low-level storage interface for storyng and retrieving events
/// </summary>
public interface IStorage
{
	Task Add(Event theEvent);
}

public abstract class StoreContext : IStoreContext
{
    // Client could fetch a list of objects and keep it pretty much forever, it will be live and synced
    // Or client can fetch something just temporarily, like and then release it to free up memory and notification pressure
}

public abstract class Event
{

}

public class ObjectCreatedEvent
{

}

public class ObjectPropertyChangedEvent
{

}

public class ObjectDeletedEvent
{

}

[Obsolete("That was used to test AOT testing")]
public class Calc
{
	public double Add(double a, double b)
	{
		return a + b;
	}
}