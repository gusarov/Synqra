namespace Synqra;

/// <summary>
/// Client → master, after its HELLO: "tell me which build you run, now and whenever that changes". Only a client that asks is sent
/// <see cref="ServerVersion"/>: a build from before it would not know that frame.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A10-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.870, "1 NodeId Guid ClientBuild string?")]
public partial class VersionRequest : TransportOperation
{
	/// <summary>The client's node (<see cref="EventReplicationState.MyNodeId"/>): a host can ask one client alone to reload.</summary>
	public required partial Guid NodeId { get; set; }

	/// <summary>The client's own build when its host knows it; null when the first <see cref="ServerVersion"/> stands for it.</summary>
	public partial string? ClientBuild { get; set; }

	public override string ToString() => $"VersionRequest({NodeId}, {ClientBuild})";
}
