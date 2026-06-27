using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Event emitted in response to a successful <see cref="RemoveLinkCommand"/>. The projection
/// applies it by removing the link from its index and notifying both former endpoints via
/// <see cref="ILinkAware"/>.
/// </summary>
[SynqraModel]
[Schema(2026.504, "1 EventId Guid CommandId Guid LinkId Guid")]
public partial class LinkRemovedEvent : Event
{
	public partial System.Guid LinkId { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
