﻿namespace Synqra;

public interface IBindableModel
{
	ISynqraStoreContext? Store { get; set; }

	/// <summary>
	/// Dedicated access to set a property by name, without using reflection.
	/// </summary>
	/// <param name="propertyName"></param>
	/// <param name="value"></param>
	void Set(string propertyName, object? value);

	/// <summary>
	/// Model<-Set<-Deserialize from a particular binary schema version. This allows to minimize the size by pre-sharing well-known schemas. Note that other named fields might follow, this is only for schema-driven fields.
	/// </summary>
	void Set(ISBXSerializer serializer, float schemaVersion, ReadOnlySpan<byte> buffer, ref int pos);

	/// <summary>
	/// Model->Get->Serialize to a particular binary schema version. This allows to minimize the size by pre-sharing well-known schemas. Note that other named fields might follow, this is only for schema-driven fields.
	/// </summary>
	void Get(ISBXSerializer serializer, float schemaVersion, Span<byte> buffer, ref int pos);
}

/// <summary>
/// Synqra Binary eXchange // or // Syncron
/// </summary>
public interface ISBXSerializer
{
	void Serialize(Span<byte> buffer, string value, ref int pos);
	void Serialize(Span<byte> buffer, in int value, ref int pos);
	string? DeserializeString(ReadOnlySpan<byte> buffer, ref int pos);
	long DeserializeSigned(ReadOnlySpan<byte> buffer, ref int pos);
	ulong DeserializeUnsigned(ReadOnlySpan<byte> buffer, ref int pos);
}

public static class BinderModes
{
	[ThreadStatic]
	public static BinderFlags Current; // default is always 000, it is always like that except when event is replayed locally
}

/// <summary>
/// Bit 0 - must be 1 for any custom value. If 0, it is default runtime state and other flags are ignored. For the cost of 1 bit we guaranteed to distinguish custom state with all toggles off from default(0) state.
/// Bit 1 - should setter raise INotifyPropertyChanged events?
/// Bit 2 - should setter produce a command in the store?
/// </summary>
public enum BinderFlags
{
	None = 0b_000, // default is always 000, so this supposed to be normal runtime state
	RaisePropertyChanged = 0b_011,
	SuppressPropertyChanged = 0b_001,
	EmitCommand = 0b_101,
	SuppressCommand = 0b_001,
}
