namespace Synqra.BinarySerializer;

/// <summary>
/// Synqra Binary eXchange serializer // or // Syncron
/// </summary>
public interface ISbxSerializer
{
	void Snapshot();
	void Reset();

	#region SERIALIZE

	void Serialize<T>(in Span<byte> buffer, in T value, ref int pos);

	void Serialize(in Span<byte> buffer, long value, ref int pos);
	void Serialize(in Span<byte> buffer, ulong value, ref int pos);
	// void Serialize(in Span<byte> buffer, string? value, ref int pos); // here string is nullable intentionally // it is disabled intentionally as nullable renamed and this one is duplicated from object nullability standpoint
	void Serialize(in Span<byte> buffer, Guid value, ref int pos); // There is no "in" for guid, becuase Guid logic uses stack copy for quick unsafe operations
	void Serialize(in Span<byte> buffer, float data, ref int pos);
	void Serialize(in Span<byte> buffer, double data, ref int pos);

	#region Nullable

	void Serialize(in Span<byte> buffer, long? data, ref int pos);
	void Serialize(in Span<byte> buffer, ulong? data, ref int pos);
	void Serialize(in Span<byte> buffer, string? data, ref int pos);
	void Serialize(in Span<byte> buffer, Guid? data, ref int pos);
	void Serialize(in Span<byte> buffer, float? data, ref int pos);
	void Serialize(in Span<byte> buffer, double? data, ref int pos);

	#endregion

	#endregion

	#region DESERIALIZE

	T Deserialize<T>(in ReadOnlySpan<byte> buffer, ref int pos);

	long DeserializeSigned(in ReadOnlySpan<byte> buffer, ref int pos);
	ulong DeserializeUnsigned(in ReadOnlySpan<byte> buffer, ref int pos);
	string DeserializeString(in ReadOnlySpan<byte> buffer, ref int pos);
	Guid DeserializeGuid(in ReadOnlySpan<byte> buffer, ref int pos);
	float DeserializeSingle(in ReadOnlySpan<byte> buffer, ref int pos);
	double DeserializeDouble(in ReadOnlySpan<byte> buffer, ref int pos);

	#region Nullable

	long? DeserializeNullableSigned(in ReadOnlySpan<byte> buffer, ref int pos);
	ulong? DeserializeNullableUnsigned(in ReadOnlySpan<byte> buffer, ref int pos);
	string? DeserializeNullableString(in ReadOnlySpan<byte> buffer, ref int pos);
	Guid? DeserializeNullableGuid(in ReadOnlySpan<byte> buffer, ref int pos);
	float? DeserializeNullableSingle(in ReadOnlySpan<byte> buffer, ref int pos);
	double? DeserializeNullableDouble(in ReadOnlySpan<byte> buffer, ref int pos);

	#endregion

	#endregion

	// it will go <T> route and will emit proper prefixes
	// void Serialize<T>(in Span<byte> buffer, in IEnumerable<T> value, ref int pos);
	IList<T> DeserializeList<T>(in ReadOnlySpan<byte> buffer, ref int pos);
	IDictionary<TK, TV> DeserializeDict<TK, TV>(in ReadOnlySpan<byte> buffer, ref int pos);
}
