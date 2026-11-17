namespace Synqra;

/// <summary>
/// Client → master, after its HELLO: "this is my node", so the master can address a reload demand to it alone. The master tells
/// its version (<see cref="ServerVersion"/>) only to a client that negotiates schemas in its HELLO or sends this.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A10-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.878, "1 NodeId Guid")]
public partial class VersionRequest : TransportOperation
{
	/// <summary>The client's node (<see cref="EventReplicationState.MyNodeId"/>): a host can ask one client alone to reload.</summary>
	public required partial Guid NodeId { get; set; }

	public override string ToString() => $"VersionRequest({NodeId})";
}
