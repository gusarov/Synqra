namespace Synqra;

/// <summary>
/// Master → client, answering a <see cref="VersionRequest"/> and pushed whenever it changes: the build the master runs and its reload
/// demand. A client whose build differs, or that sees the demand change, reloads to load the master's build.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A11-000000000000")] // family A = envelope/message (permanent); mode 9 = staging registry, pinned — this id is not committed yet
[Schema(2026.870, "1 Build string? ReloadEpoch Guid")]
public partial class ServerVersion : TransportOperation
{
	/// <summary>The master's build (its informational version), or null when its host names none.</summary>
	public partial string? Build { get; set; }

	/// <summary>Raised by the master to have its clients reload (after a schema change, say); default while it asks for none.</summary>
	public partial Guid ReloadEpoch { get; set; }

	public override string ToString() => $"ServerVersion({Build}, {ReloadEpoch})";
}
