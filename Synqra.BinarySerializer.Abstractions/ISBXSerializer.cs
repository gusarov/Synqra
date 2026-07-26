namespace Synqra.BinarySerializer;

/// <summary>
/// Synqra Binary eXchange serializer // or // Syncron
/// </summary>
public interface ISbxSerializer
{
	void Snapshot();
	void Reset();

	#region SERIALIZE

	void Serialize<T>(in Span<byte> buffer, ref int pos, in T value);

	void Serialize(in Span<byte> buffer, ref int pos, long value);
	void Serialize(in Span<byte> buffer, ref int pos, ulong value);
	// void Serialize(in Span<byte> buffer, ref int pos, string? value); // here string is nullable intentionally // it is disabled intentionally as nullable renamed and this one is duplicated from object nullability standpoint
	void Serialize(in Span<byte> buffer, ref int pos, Guid value); // There is no "in" for guid, becuase Guid logic uses stack copy for quick unsafe operations
	void Serialize(in Span<byte> buffer, ref int pos, float data);
	void Serialize(in Span<byte> buffer, ref int pos, double data);

	#region Nullable

	void Serialize(in Span<byte> buffer, ref int pos, long? data);
	void Serialize(in Span<byte> buffer, ref int pos, ulong? data);
	void Serialize(in Span<byte> buffer, ref int pos, string? data);
	void Serialize(in Span<byte> buffer, ref int pos, Guid? data);
	void Serialize(in Span<byte> buffer, ref int pos, float? data);
	void Serialize(in Span<byte> buffer, ref int pos, double? data);

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
	// void Serialize<T>(in Span<byte> buffer, ref int pos, in IEnumerable<T> value);
	IList<T> DeserializeList<T>(in ReadOnlySpan<byte> buffer, ref int pos);
	IDictionary<TK, TV> DeserializeDict<TK, TV>(in ReadOnlySpan<byte> buffer, ref int pos);
}
