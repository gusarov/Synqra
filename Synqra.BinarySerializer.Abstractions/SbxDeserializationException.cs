namespace Synqra.BinarySerializer;

/// <summary>
/// An SBX record this build cannot deserialize: it names no schemas, a schemas bundle or type this build does not have, or bytes
/// that do not decode. Synqra only reports it; what it means (a client update, a cache to drop) is the host's to decide.
/// </summary>
public class SbxDeserializationException : Exception
{
	public SbxDeserializationException(string record, string? schemas, Exception innerException)
		: base($"SBX record {record} (written with {schemas ?? "no schemas bundle"}) cannot be deserialized by this build: {innerException.Message}", innerException)
	{
		Record = record;
		Schemas = schemas;
	}

	/// <summary>The record, as its store names it.</summary>
	public string Record { get; }

	/// <summary>The schemas bundle the record names, or null when it names none.</summary>
	public string? Schemas { get; }
}
