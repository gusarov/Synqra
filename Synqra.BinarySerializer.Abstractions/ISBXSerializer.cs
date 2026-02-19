namespace Synqra.BinarySerializer;

/// <summary>
/// Synqra Binary eXchange serializer // or // Syncron
/// </summary>
public interface ISBXSerializer
{
	void Snapshot();
	void Reset();

	void Serialize<T>(in Span<byte> buffer, T value, ref int pos);

	void Serialize(in Span<byte> buffer, string value, ref int pos);
	void Serialize(in Span<byte> buffer, in long value, ref int pos);
	void Serialize(in Span<byte> buffer, ulong value, ref int pos);
	// it will go <T> route and will emit proper prefixes
	// void Serialize<T>(in Span<byte> buffer, in IEnumerable<T> value, ref int pos);

	T Deserialize<T>(in ReadOnlySpan<byte> buffer, ref int pos);

	string? DeserializeString(in ReadOnlySpan<byte> buffer, ref int pos);
	long DeserializeSigned(in ReadOnlySpan<byte> buffer, ref int pos);
	ulong DeserializeUnsigned(in ReadOnlySpan<byte> buffer, ref int pos);
	IList<T> DeserializeList<T>(in ReadOnlySpan<byte> buffer, ref int pos);
	IDictionary<TK, TV> DeserializeDict<TK, TV>(in ReadOnlySpan<byte> buffer, ref int pos);
}
