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
public partial class ObjectCreatedEvent : SingleObjectEvent
{
	// public partial IDictionary<string, object?>? Data { get; set; }
	public partial object? Data { get; set; }

	[JsonIgnore]
	public object? DataObject { get; set; } // InMemory materialized object

	partial void OnDataChanging(object? oldValue, object? value)
	{
		if (value is IDictionary<string, object?> dict)
		{
			throw new NotImplementedException();
		}
		else if (value is string s)
		{
			throw new NotImplementedException();
		}
		else if (value is IBindableModel bm)
		{
			// This is allowed for now. Caution - it is not read only and might be changed after the event is created, which can lead to unexpected behavior.
			// It is recommended to use immutable data structures for event data to ensure consistency and reliability.
		}
		else
		{
			// PoCo is also allowed for now, because there are already existing tests
			// throw new NotImplementedException();
		}
	}

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
