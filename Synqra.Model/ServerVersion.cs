namespace Synqra;

/// <summary>
/// Master → client, first after its HELLO reply to a client that negotiates schemas, and pushed whenever it changes: the build the
/// master runs, its reload demand and its schemas bundle. A client on another schemas bundle, or that sees the
/// demand change, stops and reloads onto the master's build.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A11-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.878, "1 Build string? ReloadEpoch Guid SchemasBundleId long SchemasBundleContent byte[]?")]
public partial class ServerVersion : TransportOperation
{
	/// <summary>The master's build (its informational version), or null when its host names none.</summary>
	public partial string? Build { get; set; }

	/// <summary>Raised by the master to have its clients reload (after a schema change, say); default while it asks for none.</summary>
	public partial Guid ReloadEpoch { get; set; }

	/// <summary>The master's schemas bundle id (SchemasBundle.Id): a client on another bundle cannot read its frames, nor it the client's.</summary>
	public partial long SchemasBundleId { get; set; }

	/// <summary>The bundle's content, so a peer that does not know the id has the whole mapping.</summary>
	public partial byte[]? SchemasBundleContent { get; set; }

	public override string ToString() => $"ServerVersion({Build}, {ReloadEpoch}, schemas {SchemasBundleId:x8})";
}
