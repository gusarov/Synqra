using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// A wire was removed. The projection drops it from in-memory state and from
/// the routing index.
/// </summary>
[SynqraModel]
[Schema(2026.406, "1 EventId Guid CommandId Guid WireId Guid")]
public partial class WireDeletedEvent : Event
{
	public partial System.Guid WireId { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
