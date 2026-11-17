namespace Synqra.BinarySerializer;

/// <summary>
/// The schema version each map id is written and read at: a stored record names the versions it was written with, and a
/// later build reads it at those, not at its own latest.
/// </summary>
public interface ISbxSchemaVersions
{
	/// <summary>Every bindable map id, at the version it is written and read at now: a build's schema set.</summary>
	IReadOnlyCollection<KeyValuePair<int, float>> MappedSchemaVersions { get; }

	/// <summary>The map ids written with since the last <see cref="ForgetUsage"/>, each at the version it was written.</summary>
	IReadOnlyCollection<KeyValuePair<int, float>> UsedSchemaVersions { get; }

	/// <summary>Starts a new record: tracks from here the map ids it writes with.</summary>
	void ForgetUsage();

	/// <summary>The version a map id is read and written at now, or null when the id is not mapped.</summary>
	float? FindSchemaVersion(int mapId);

	/// <summary>Reads and writes an already mapped id at <paramref name="schemaVersion"/> from now on.</summary>
	void RemapSchemaVersion(int mapId, float schemaVersion);
}
