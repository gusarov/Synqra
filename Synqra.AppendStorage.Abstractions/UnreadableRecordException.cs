namespace Synqra.AppendStorage;

/// <summary>A stored record this build cannot read back: its key, its size and what its header says it was written with.</summary>
public class UnreadableRecordException : Exception
{
	public UnreadableRecordException(string key, int length, string? schemas, Exception innerException)
		: base($"Record {key} ({length} bytes, written with {schemas ?? "no schema header"}) cannot be read by this build: {innerException.Message}", innerException)
	{
		Key = key;
		Length = length;
		Schemas = schemas;
	}

	/// <summary>The record's key as text.</summary>
	public string Key { get; }

	public int Length { get; }

	/// <summary>The map ids and schema versions the record's header names, or null for a record written before the header.</summary>
	public string? Schemas { get; }
}
