using System.Text.Json.Serialization;

namespace Synqra;

[JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators = false, TypeDiscriminatorPropertyName = "_t", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
// [KnownType(typeof(ObjectCreatedEvent))]
// [KnownType(typeof(ObjectPropertyChangedEvent))]
// [JsonSerializable(typeof(ObjectCreatedEvent))]
// [JsonSerializable(typeof(ObjectPropertyChangedEvent))]
[JsonDerivedType(typeof(ObjectCreatedEvent), "ObjectCreatedEvent")]
[JsonDerivedType(typeof(ObjectPropertyChangedEvent), "ObjectPropertyChangedEvent")]
[JsonDerivedType(typeof(ObjectDeletedEvent), "ObjectDeletedEvent")]
[JsonDerivedType(typeof(CommandCreatedEvent), "CommandCreatedEvent")]
public abstract class Event : IIdentifiable<Guid>
{
	Guid IIdentifiable<Guid>.Id => EventId;

	public required Guid EventId { get; set; }
	public required Guid CommandId { get; set; }
	// public required Guid UserId { get; set; }
	[JsonIgnore] // too verbose, containerId (streamId) should be handled outside of event stream
	public Guid ContainerId { get; set; } // like layer id

	/*
	public async Task AcceptAsync<T>(IEventVisitor<object?> visitor)
	{
		await visitor.BeforeVisitAsync(this, null);
		await AcceptCoreAsync(visitor, null);
		await visitor.AfterVisitAsync(this, null);
	}
	*/

	public async Task AcceptAsync<T>(IEventVisitor<T> visitor, T ctx)
	{
		await visitor.BeforeVisitAsync(this, ctx);
		await AcceptCoreAsync(visitor, ctx);
		await visitor.AfterVisitAsync(this, ctx);
	}

	protected abstract Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx);
}
