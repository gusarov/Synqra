using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Detach a wire by id. No-op if the wire doesn't exist.
/// </summary>
[SynqraModel]
[Schema(2026.406, "1 CommandId Guid StreamId Guid WireId Guid")]
public partial class DeleteWireCommand : Command
{
	public partial System.Guid WireId { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
