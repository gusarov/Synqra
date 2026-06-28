using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Event emitted in response to a successful <see cref="AddLinkCommand"/>. The projection applies
/// it by materializing the link (reusing the live instance when applied locally, or rehydrating
/// from <see cref="Data"/> on replay), structurally deduping it, indexing it, attaching it to the
/// store, and notifying both endpoints via <see cref="ILinkAware"/> so their navigation properties
/// raise <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>.
/// <para>
/// <see cref="SourceId"/>/<see cref="TargetId"/> are explicit, mandatory fields — see
/// <see cref="AddLinkCommand"/>'s remarks for why. They are authoritative: applying this event
/// sets the materialized link's own <c>SourceId</c>/<c>TargetId</c> from these fields (not the
/// other way around), so indexing never has to materialize <see cref="Data"/> first to learn
/// what the link connects.
/// </para>
/// </summary>
[SynqraModel]
[Schema(2026.502, "1 EventId Guid CommandId Guid LinkTypeId Guid LinkId Guid SourceId Guid TargetId Guid Data ObjectData")]
public partial class LinkAddedEvent : Event
{
	public partial System.Guid LinkTypeId { get; set; }
	public partial System.Guid LinkId { get; set; }

	/// <summary>Identity of the object at the link's source/from end. Mandatory, authoritative over whatever <see cref="Data"/> carries.</summary>
	public partial System.Guid SourceId { get; set; }

	/// <summary>Identity of the object at the link's target/to end. Mandatory, authoritative over whatever <see cref="Data"/> carries.</summary>
	public partial System.Guid TargetId { get; set; }

	/// <summary>The concrete link subtype's own extra properties, if any — never LinkId/SourceId/TargetId (see <see cref="AddLinkCommand"/>'s remarks).</summary>
	public required partial ObjectData Data { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}
