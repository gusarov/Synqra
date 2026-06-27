using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Remove an existing link, addressed by its own <see cref="LinkId"/> — links have no container,
/// so there is no (container, type, id) triple to address by the way <see cref="DeleteComponentCommand"/>
/// addresses a component.
/// </summary>
[SynqraModel]
[Schema(2026.503, "1 CommandId Guid StreamId Guid LinkId Guid")]
public partial class RemoveLinkCommand : Command
{
	public partial System.Guid LinkId { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
