using System.Text.Json.Serialization;

namespace Synqra;

[SynqraModel]
[Schema(2025.789, "1 Data IDictionary<string, object?>? DataString string? DataObject object? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.790, "1 Data IDictionary<string, object?>? DataString string? DataObject object? EventId Guid CommandId Guid ContainerId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.791, "1 Data IDictionary<string, object?>? DataString string? DataObject object? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.792, "1 Data IDictionary<string, object?>? DataString string? DataObject object? EventId Guid CommandId Guid ContainerId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.793, "1 Data IDictionary<string, object?>? DataString string? DataObject object? EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.794, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.795, "1 Data IDictionary<string, object?>? DataString string? DataObject object? EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.796, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.797, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data IDictionary<string, object?>?")]
[Schema(2025.798, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.799, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data IDictionary<string, object?>?")]
[Schema(2025.800, "1 Data IDictionary<string, object?>? DataString string? DataObject object? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.801, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data IDictionary<string, object?>?")]
[Schema(2025.802, "1 Data IDictionary<string, object?>? DataString string? DataObject object? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.803, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data IDictionary<string, object?>?")]
[Schema(2025.804, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.805, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data object?")]
[Schema(2026.161, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data object? DataObject object?")]
[Schema(2026.162, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data object?")]
[Schema(2026.167, "1 EventId Guid CommandId Guid ContainerId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data object? DataObject object?")]
[Schema(2026.168, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data object?")]
[Schema(2026.169, "1 EventId Guid CommandId Guid ContainerId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data object? DataObject object?")]
[Schema(2026.170, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid Data ObjectData")]
public partial class ObjectCreatedEvent : SingleObjectEvent
{
	/// <summary>
	/// Canonical property-bag payload for the created object — always present, though it may be
	/// empty. The locally-emitted create path additionally carries the live instance in
	/// <see cref="MaterializedObject"/> so the projection can skip rebuilding from this bag.
	/// </summary>
	public required partial ObjectData Data { get; set; }

	/// <summary>
	/// In-memory materialized instance, set on the locally-emitted create path so the
	/// projection can attach the very object the caller created instead of rebuilding it
	/// from <see cref="Data"/>. Never serialized — it is a process-local optimization only.
	/// </summary>
	[JsonIgnore]
	public object? MaterializedObject { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
