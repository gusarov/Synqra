namespace Synqra;

public interface IEvent
{
}

public interface IGuidentifiable : IIdentifiable<Guid>
{
}

public interface IIdentifiable<TKey> where TKey : notnull
{
	public TKey Id { get; }
}
