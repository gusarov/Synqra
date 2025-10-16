using System.Text.Json.Serialization;

namespace Synqra;

[SynqraModel]
[Schema(2025.789, "1 Data IDictionary<string, object?>? DataString string? DataObject object? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.790, "1 Data IDictionary<string, object?>? DataString string? DataObject object? EventId Guid CommandId Guid ContainerId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.791, "1 Data IDictionary<string, object?>? DataString string? DataObject object? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.792, "1 Data IDictionary<string, object?>? DataString string? DataObject object? EventId Guid CommandId Guid ContainerId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.793, "1 Data IDictionary<string, object?>? DataString string? DataObject object? EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
public partial class ObjectCreatedEvent : SingleObjectEvent
{
	public partial IDictionary<string, object?>? Data { get; set; }

	[JsonIgnore]
	public partial string? DataString { get; set; }

	[JsonIgnore]
	public partial object? DataObject { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
