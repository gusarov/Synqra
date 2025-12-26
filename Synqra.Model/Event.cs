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
[SynqraModel]
[Schema(2025.789, "1 EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.793, "1")]
[Schema(2025.794, "1 EventId Guid CommandId Guid")]
[Schema(2025.795, "1")]
[Schema(2025.796, "1 EventId Guid CommandId Guid")]
[Schema(2025.797, "1 EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.798, "1 EventId Guid CommandId Guid")]
[Schema(2025.799, "1 EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.800, "1 EventId Guid CommandId Guid")]
public abstract partial class Event : IEvent
{
	// Guid IIdentifiable<Guid>.Id => EventId;

	public required partial Guid EventId { get; set; }
	public required partial Guid CommandId { get; set; }
	// public required Guid UserId { get; set; }
	[JsonIgnore] // too verbose, containerId (streamId) should be handled outside of event stream
	public partial Guid ContainerId { get; set; } // like layer id

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

	public override string? ToString()
	{
		var ts = base.ToString();
		return true 