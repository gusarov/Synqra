using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Numerics;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Unicode;

namespace Synqra.BinarySerializer;

public class SBXSerializer
{
	public enum TypeId
	{
		Unknown = 0,
		AsRequested = -1, // same type as requested
		// System primitives going down
		SignedInteger = -2,
		UnsignedInteger = -3,
		Utf8String = -4,
	}

	static UTF8Encoding _utf8 = new(false, true)
	{
		// EncoderFallback = EncoderFallback.ExceptionFallback,
		// DecoderFallback = DecoderFallback.ExceptionFallback,
	};

	/*
	public int Serialize(Span<byte> buffer, object data)
	{
		switch (data)
		{
			case int i:
				return Serialize(ref buffer, TypeId.SignedInteger) Serialize(ref buffer, i);
			case uint u:
				return Serialize(buffer, u);
			case string s:
				return Serialize(buffer, s);
			default:
				throw new Exception($"Type {data.GetType().FullName} is not supported for serialization.");
		}
	}
	*/

	#region Object

	public void Serialize<T>(Span<byte> buffer, T obj, ref int pos)
	{
		Serialize(buffer, obj, ref pos, typeof(T));
	}

	public void Serialize(Span<byte> buffer, object obj, ref int pos, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type requestedType)
	{
		// If this is level 1 (precise custom) then schema id. Schema ID always assumes a class name (type).
		// If this is level 2 (field names) then 0 (schemaid=0) and the first field is the type name string.
		// If this is level 3, the reset of the fields are in alphabetical order by field name (to ensure canonical normalization for auto-repairs & hash tree)

		var type = obj?.GetType() ?? throw new ArgumentNullException();
		var typeId = GetTypeId(type);
		if (requestedType != type)
		{
			Serialize(buffer, (int)typeId, ref pos);
			if (typeId == 0)
			{
				var aqn = type.AssemblyQualifiedName;
				aqn = aqn[0..aqn.IndexOf(',', aqn.IndexOf(',') + 1)]; // trim assembly part

				Serialize(buffer, aqn ?? throw new NotSupportedException(), ref pos);
			}
		}
		else
		{
			Serialize(buffer, TypeId.AsRequested, ref pos);
		}

		if (obj is IBindableModel bm)
		{
			throw new NotSupportedException();
		}
		else if (obj is int i)
		{
			Serialize(buffer, i, ref pos);
		}
		else if (obj is uint u)
		{
			Serialize(buffer, u, ref pos);
		}
		else if (obj is string s)
		{
			Serialize(buffer, s, ref pos);
		}
		else
		{
			// Only generated bindable models are supported for Level 1 & 2 serialization. All other types are Level 3 (field names).
			foreach (var item in type.GetProperties())
			{
				var val = item.GetValue(obj);
				if (val != null)
				{
					Serialize(buffer, item.Name, ref pos);
					Serialize(buffer, val, ref pos);
				}
			}
		}
	}

	public T Deserialize<T>(ref Span<byte> buffer)
	{
		return (T)Deserialize(ref buffer, typeof(T));
	}

	public object Deserialize(ref Span<byte> buffer, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type? requestedType = null)
	{
		var ros = (ReadOnlySpan<byte>)buffer;
		var res = Deserialize(ref ros, requestedType);
		buffer = buffer[(buffer.Length - ros.Length)..];
		return res;
	}

	public object Deserialize(ReadOnlySpan<byte> buffer, ref int pos, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type? requestedType = null)
	{
		var buf = buffer[pos..];
		var obj = Deserialize(ref buf, requestedType);
		pos += buffer.Length - buf.Length;
		return obj;
	}

	public object Deserialize(ref ReadOnlySpan<byte> buffer, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type? requestedType = null)
	{
		// read type
		var typeId = (TypeId)DeserializeSigned(ref buffer);
		Type type;
		object value;
		if (typeId == 0)
		{
			type = Type.GetType(DeserializeString(ref buffer), true);
		}
		else if (typeId == TypeId.AsRequested)
		{
			type = requestedType;
		}
		else if (typeId == TypeId.SignedInteger)
		{
			return DeserializeSigned(ref buffer);
		}
		else if (typeId == TypeId.UnsignedInteger)
		{
			return DeserializeUnsigned(ref buffer);
		}
		else if (typeId == TypeId.Utf8String)
		{
			return DeserializeString(ref buffer);
		}
		else
		{
			throw new Exception("Unsupported Type Id " + typeId);
		}
		value = Activator.CreateInstance(type) ?? throw new Exception("Could not create instance of type " + type.FullName);

		// free key-value pairs until the end of buffer or untill comment
		while (buffer.Length > 0)
		{
			// keep reading properties as strings
			var propName = DeserializeString(ref buffer);
			if (propName.StartsWith("//"))
			{
				break; // drop the comment at the end of buffer
			}

			/*
			var propTypeId = (TypeId)DeserializeSigned(ref buffer);
			Type propType;
			if (propTypeId == 0)
			{
				propType = Type.GetType(DeserializeString(ref buffer), true);
			}
			else
			{
				propType = GetTypeFromId(propTypeId) ?? throw new Exception();
			}
			*/
			var propValue = Deserialize(ref buffer);
			// Set the property value using reflection
			if (value is IBindableModel bm)
			{
				bm.Set(propName, propValue);
			}
			else
			{
				var prop = type.GetProperty(propName);
				if (prop != null && prop.CanWrite)
				{
					if (propValue.GetType() != prop.PropertyType)
					{
						if (prop.PropertyType.IsEnum && propValue is long l)
						{
							propValue = Enum.ToObject(prop.PropertyType, l);
						}
						else
						{
							propValue = Convert.ChangeType(propValue, prop.PropertyType);
						}
					}
					prop.SetValue(value, propValue);
				}
			}
		}

		return value;
	}

	private object Deserialize(ref ReadOnlySpan<byte> buffer, int typeId)
	{
		switch (typeId)
		{
			case (int)TypeId.Utf8String:
				return DeserializeString(ref buffer);
			case (int)TypeId.SignedInteger:
			// return DeserializeSigned(ref buffer);
			case (int)TypeId.UnsignedInteger:
				return DeserializeUnsigned(ref buffer);
			default:
				throw new Exception("Unsupported Type Id");
		}
	}

	#endregion

	#region Type

	private TypeId GetTypeId(Type type)
	{
		switch (type)
		{
			case Type t when t == typeof(sbyte) || t == typeof(short) || t == typeof(int) || t == typeof(long) || t == typeof(BigInteger):
				return TypeId.SignedInteger; // Signed Integers
			case Type t when t == typeof(byte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong):
				return TypeId.UnsignedInteger; // Unsigned Integers
			case Type t when t == typeof(string):
				return TypeId.Utf8String;
			default:
				return TypeId.Unknown;
				// throw new Exception($"Type {type.FullName} is not supported for serialization.");
		}
	}

	private Type GetTypeFromId(TypeId typeId)
	{
		return typeId switch
		{
			TypeId.SignedInteger => typeof(long),
			TypeId.UnsignedInteger => typeof(ulong),
			TypeId.Utf8String => typeof(string),
			_ => throw new Exception($"TypeId {typeId} is not supported for deserialization."),
		};
	}

	public void Serialize(Span<byte> buffer, Type type, ref int pos)
	{
		var typeId = GetTypeId(type);
		Serialize(buffer, typeId, ref pos);
		if (typeId == TypeId.Unknown)
		{
			var typeName = type.FullName ?? throw new Exception("Type has no full name: " + type.Name);
			Serialize(buffer, typeName, ref pos);
		}
	}

	public Type Deserialize(Span<byte> buffer, ref int pos)
	{
		var typeId = (TypeId)DeserializeSigned(buffer, ref pos);

		if (typeId == TypeId.Unknown)
		{
			var typeName = DeserializeString(buffer, ref pos);
			return Type.GetType(typeName, true);
		}
		else
		{
			return GetTypeFromId(typeId);
		}
	}

	#endregion

	#region String v2 (NullTerm)

	public string DeserializeString(ReadOnlySpan<byte> buffer, ref int pos)
	{
		int length = MemoryExtensions.IndexOf<byte>(buffer, 0); // todo: it probably allocates array here :(
		if (length < 0)
		{
			throw new ArgumentException("Buffer too small for string decoding.");
		}

		var str = _utf8.GetString(buffer[pos..][..length]);
		pos += length + 1;
		return str;
	}

	public string DeserializeString(ref ReadOnlySpan<byte> buffer)
	{
		int length = MemoryExtensions.IndexOf<byte>(buffer, 0); // todo: it probably allocates array here :(
		// var length = (int)DeserializeUnsigned(ref buffer);
		if (length < 0)
		{
			throw new ArgumentException("Buffer too small for string decoding.");
		}
		var str = _utf8.GetString(buffer[0..length]);
		buffer = buffer[(length + 1)..];
		return str;
	}

	public void Serialize(Span<byte> buffer, string data, ref int pos)
	{
		// Serialize(buffer, (uint)data.Length, ref pos);
		pos += _utf8.GetBytes(data, buffer[pos..]);
		buffer[pos++] = 0; // null terminator
	}

	public void Serialize(ref Span<byte> buffer, string data)
	{
		// Serialize(buffer, (uint)data.Length, ref pos);
		var pos = _utf8.GetBytes(data, buffer);
		buffer[pos++] = 0; // null terminator
		buffer = buffer[pos..];
	}

	public static int IndexOfNull(ReadOnlySpan<byte> span)
	{
		// TODO: it probably allocates array here :( need more optimization
		return Array.IndexOf(span.ToArray(), (byte)0);
	}

	#endregion

	#region Signed Integer

	public void Serialize(Span<byte> buffer, in int data, ref int pos)
	{
		// ZigZag encode the signed int
		uint zigzag = (uint)((data << 1) ^ (data >> 31));
		while (zigzag >= 0x80)
		{
			if (pos >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint encoding.");
			buffer[pos++] = (byte)((zigzag & 0x7F) | 0x80);
			zigzag >>= 7;
		}
		if (pos >= buffer.Length)
			throw new ArgumentException("Buffer too small for varint encoding.");
		buffer[pos++] = (byte)zigzag;
	}

	public void Serialize(ref Span<byte> buffer, in int data)
	{
		// ZigZag encode the signed int
		uint zigzag = (uint)((data << 1) ^ (data >> 31));
		int count = 0;
		while (zigzag >= 0x80)
		{
			if (count >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint encoding.");
			buffer[count++] = (byte)((zigzag & 0x7F) | 0x80);
			zigzag >>= 7;
		}
		if (count >= buffer.Length)
			throw new ArgumentException("Buffer too small for varint encoding.");
		buffer[count++] = (byte)zigzag;
		buffer = buffer[count..];
	}

	public long DeserializeSigned(ref ReadOnlySpan<byte> buffer)
	{
		ulong raw = DeserializeUnsigned(ref buffer);
		long temp = (long)(raw >> 1);
		if ((raw & 1) != 0)
		{
			temp = ~temp;
		}
		return temp;
	}

	public long DeserializeSigned(ReadOnlySpan<byte> buffer, ref int pos)
	{
		ulong raw = DeserializeUnsigned(buffer, ref pos);
		long temp = (long)(raw >> 1);
		if ((raw & 1) != 0)
		{
			temp = ~temp;
		}
		return temp;
	}

	#endregion

	#region Unsigned Integer

	public void Serialize(Span<byte> buffer, in uint data, ref int pos)
	{
		// Protobuf-style varint encoding for uint32 (little-endian, 7 bits per byte, MSB=1 means more)
		uint value = data;
		while (value >= 0x80)
		{
			if (pos >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint encoding.");
			buffer[pos++] = (byte)((value & 0x7F) | 0x80);
			value >>= 7;
		}
		if (pos >= buffer.Length)
			throw new ArgumentException("Buffer too small for varint encoding.");
		buffer[pos++] = (byte)value;
	}

	public void Serialize(ref Span<byte> buffer, in uint data)
	{
		// Protobuf-style varint encoding for uint32 (little-endian, 7 bits per byte, MSB=1 means more)
		int count = 0;
		uint value = data;
		while (value >= 0x80)
		{
			if (count >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint encoding.");
			buffer[count++] = (byte)((value & 0x7F) | 0x80);
			value >>= 7;
		}
		if (count >= buffer.Length)
			throw new ArgumentException("Buffer too small for varint encoding.");
		buffer[count++] = (byte)value;
		buffer = buffer[count..];
	}

	public ulong DeserializeUnsigned(ReadOnlySpan<byte> buffer, ref int pos)
	{
		ulong result = 0;
		int shift = 0;
		while (true)
		{
			if (pos >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint decoding.");
			byte b = buffer[pos++];
			result |= (ulong)(b & 0x7F) << shift;
			if ((b & 0x80) == 0)
				break;
			shift += 7;
			if (shift > 63)
				throw new FormatException("Varint is too long.");
		}
		return result;
	}

	public ulong DeserializeUnsigned(ref ReadOnlySpan<byte> buffer)
	{
		ulong result = 0;
		int shift = 0;
		int count = 0;
		while (true)
		{
			if (count >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint decoding.");
			byte b = buffer[count++];
			result |= (ulong)(b & 0x7F) << shift;
			if ((b & 0x80) == 0)
				break;
			shift += 7;
			if (shift > 63)
				throw new FormatException("Varint is too long.");
		}
		buffer = buffer.Slice(count);
		return result;
	}

	#endregion

}

