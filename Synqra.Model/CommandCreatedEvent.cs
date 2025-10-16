
namespace Synqra;

[SynqraModel]
[Schema(2025.791, "1 EventId Guid CommandId Guid")]
[Schema(2025.792, "1 EventId Guid CommandId Guid Data Command")]
[Schema(2025.793, "1 EventId Guid CommandId Guid")]
[Schema(2025.794, "1 EventId Guid CommandId Guid Data Command")]
[Schema(2025.795, "1 Data Command EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.796, "1 EventId Guid CommandId Guid Data Command")]
[Schema(2025.797, "1 Data Command EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.798, "1 EventId Guid CommandId Guid Data Command")]
public partial class CommandCreatedEvent : Event
{
	public required partial Command Data { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
