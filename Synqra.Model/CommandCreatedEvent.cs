
namespace Synqra;

public class CommandCreatedEvent : Event, IBindableModel
{
	public required Command Data { get; init; }

	public ISynqraStoreContext? Store { get; set; }

	public void Get(ISBXSerializer serializer, float schemaVersion, in Span<byte> buffer, ref int pos)
	{
		throw new NotImplementedException();
	}

	public void Set(string propertyName, object? value)
	{
		throw new NotImplementedException();
	}

	public void Set(ISBXSerializer serializer, float schemaVersion, in ReadOnlySpan<byte> buffer, ref int pos)
	{
		throw new NotImplementedException();
	}

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
