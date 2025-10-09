using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Numerics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace Synqra.BinarySerializer;

public class SBXSerializer
{
	// A schema can carry not only the mappings but also - a day when it was created. This can let automatically expire old schemas. E.g. just a simple rule that no stream can live for more than a year and have to be re-built, leads to a fact that we know, a year old format can be easily dropped if it is not latest.

	// StaticMap|PresenceMaskLayout|fieldIds

	// StaticMap - Id:zig,Name:str
	// PresenceMaskLayout
	// fieldIds

	// [SchemaVersion(2025.15, "Id:zig|IsActive:exact_bool,Level:uns|LevelName:1")]
	// [SchemaVersion(2025.16, "Id:zig,Name:str")]
	// partial class SomeModel : IBindableObject
	// {
	//     public int Id { get; set; }
	//     public string Name { get; set; }
	//     public bool IsActive { get; set; }
	//     public int? Level { get; set; }
	//     public string? LevelName { get; set; }
	// }

	public enum TypeId
	{
		Unknown = 0,
		AsRequested = -1, // same type as requested
		// System primitives going down
		SignedInteger = -2,
		UnsignedInteger = -3,
		Utf8String = -4,
		Guid = -5,
	}

	static UTF8Encoding _utf8 = new(false, true)
	{
		// EncoderFallback = EncoderFallback.ExceptionFallback,
		// DecoderFallback = DecoderFallback.ExceptionFallback,
	};

	// This is to compress time in streams. All time can now be calculatead as varbinary of this custom epoch (when stream started). This is to save space.
	DateTime _streamBaseTime = DateTime.UtcNow;
	Dictionary<int, Type> _typeById = new();
	Dictionary<Type, int> _idByType = new();

	public void SetTimeBase(DateTime streamBaseTime)
	{
		if (streamBaseTime.Kind != DateTimeKind.Utc)
		{
			throw new ArgumentException("Stream base time must be in UTC.");
		}
		_streamBaseTime = streamBaseTime;
	}

	public void Map(int typeId, Type type)
	{
		_typeById[typeId] = type;
		_idByType[type] = typeId;
	}

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
			Serialize(buffer, (int)TypeId.AsRequested, ref pos);
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
		else if (obj is Guid g)
		{
			Serialize(buffer, g, ref pos);
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

	public T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> buffer, ref int pos)
	{
		return (T)Deserialize(buffer, ref pos, typeof(T));
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
		else if (typeId == TypeId.Guid)
		{
			var pos = 0;
			var g = DeserializeGuid(buffer, ref pos);
			buffer = buffer[pos..];
			return g;
		}
		else
		{
			if (_typeById.TryGetValue((int)typeId, out var t))
			{
				type = t;
			}
			else
			{
				throw new Exception("Unsupported Type Id " + typeId);
			}
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
					if (!prop.PropertyType.IsAssignableFrom(propValue.GetType()))
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

	/*
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
	*/

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
			case Type t when t == typeof(Guid):
				return TypeId.Guid;
			default:
				_idByType.TryGetValue(type, out var id); // it will stay Unknown if there is no such type, as designed.
				return (TypeId)id;
				// throw new Exception($"Type {type.FullName} is not supported for serialization.");
		}
	}

	private Type GetTypeFromId(TypeId typeId)
	{
		if (_typeById.TryGetValue((int)typeId, out var type))
		{
			return type;
		}
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

	private void Serialize(Span<byte> buffer, in TypeId data, ref int pos)
	{
		Serialize(buffer, (int)data, ref pos);
	}

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

	public unsafe void Serialize(Span<byte> buffer, Guid data, ref int pos)
	{
		// glyph byte corresponds to 8th byte of the GUID
		// but it is more powerful, because it makes use of unused or legacy space
		// Legacy guids (Apollo 1980, Microsoft COM 1990-2000) are still supported but it will take 1 extra byte space as a cost to support this nonsense.
		// For Apollo - this immediately gives back all values 0-127 that can be used for special meanings in the future.

		// We can also claim Version 0 (all zeros) in 6th byte, which is currently unused. But for now 8th byte is good enough.

		// GLYPH:
		// 00000000 00 nil (that's it, the guid is nil, no other bytes)
		// 00000001 01 max
		// 00000010 02 fallback 17 bytes mode (any Legacy or non standard guid)
		// 00000011 03 RESERVED
		// 000001xx 04-07 v7 time based compressed +2 bits from rand_a
		// 001xxxxx v8 5 section compression
		// 1xxxxxxx Guid as in RFC (mixed endian for .Net performance on LE, except that this byte goes to 8 and the rest is picked from/to RAM sequentially) This ensures we can store any standard guid in 16 bytes max.

		byte* bytes = (byte*)&data;
		if (data == default)
		{
			buffer[pos++] = 0; // glyph 0 - empty guid
		}
		else if (data == _guidMax)
		{
			buffer[pos++] = 1; // glyph 1 - max guid
		}
		else if (data.GetVariant() != 1)
		{
			// We took away high bit of byte 8, so we capture Apollo 1980 & MS Legacy Guids. This means such guid can only be written with 17 bytes instead of 16. Glyph 2 will mark that.
			buffer[pos++] = 2; // glyph 2 - Fallback to 17 bytes guid (a payment for the luxury to have a glyph). Same technique can serialize any other missed guid, e.g. v0
			for (int i = 0; i < 16; i++)
			{
				buffer[pos++] = bytes[i];
			}
		}
		else
		{
			if (data.GetVariant() == 1)
			{
				switch (data.GetVersion())
				{
					case 8:
					{
						// for now it is 5 empty sections compression
						uint* uints = (uint*)&data;
						ushort* ushorts = (ushort*)&data;

						bytes[8] &= 0x3F; // remove variant
						bytes[7] &= 0x0F; // remove version

						byte glyph = 0b00100000;
						var glyphPos = pos++;
						if (uints[0] != 0) // #4
						{
							//            43210
							glyph |= 0b00010000;
							MemoryMarshal.Write(buffer[pos..], in uints[0]);
							pos += 4;
						}
						if (ushorts[2] != 0) // #3
						{
							//             3210
							glyph |= 0b00001000;
							MemoryMarshal.Write(buffer[pos..], in ushorts[2]);
							pos += 2;
						}
						if (ushorts[3] != 0) // #2
						{
							//              210
							glyph |= 0b00000100;
							MemoryMarshal.Write(buffer[pos..], in ushorts[3]);
							pos += 2;
						}
						if (ushorts[4] != 0) // #1
						{
							//               10
							glyph |= 0b00000010;
							MemoryMarshal.Write(buffer[pos..], in ushorts[4]);
							pos += 2;
						}
						if (uints[3] != 0 || ushorts[5] != 0) // #0
						{
							//                0
							glyph |= 0b00000001;
							MemoryMarshal.Write(buffer[pos..], in ushorts[5]); // this is likely wrong and will rearrange bytes! Because windows do only int-short-short
							pos += 2;
							MemoryMarshal.Write(buffer[pos..], in bytes[12]); // this is likely wrong and will rearrange bytes!
							pos += 1;
							MemoryMarshal.Write(buffer[pos..], in bytes[13]); // this is likely wrong and will rearrange bytes!
							pos += 1;
							MemoryMarshal.Write(buffer[pos..], in bytes[14]); // this is likely wrong and will rearrange bytes!
							pos += 1;
							MemoryMarshal.Write(buffer[pos..], in bytes[15]); // this is likely wrong and will rearrange bytes!
							pos += 1;
						}
						buffer[glyphPos] = glyph;
						return;
					}
					case 7:
					{
						buffer[pos++] = (byte)((1<<2) | (bytes[7] & 0x03)); // 4-7: UUIDv7 - time based + 2 bits from rand_a_high
						var ms = data.GetTimestamp();
						var tsdiff = checked((int)((ms - _streamBaseTime).TotalMilliseconds));
						Serialize(buffer, tsdiff, ref pos);
						buffer[pos++] = bytes[6]; // rand_a_low
						buffer[pos++] = (byte)(((bytes[7] & 0x0C)<<4) | (bytes[8] & 0x3F)); // packed8 rand_a_high 2 other bit + byte8
						for (int i = 9; i < 16; i++)
						{
							buffer[pos++] = bytes[i];
						}
						return;
					}
				}
			}
			// fallback
			buffer[pos++] = bytes[8];
			for (int i = 0; i < 8; i++)
			{
				buffer[pos++] = bytes[i];
			}
			for (int i = 9; i < 16; i++)
			{
				buffer[pos++] = bytes[i];
			}
		}
	}

	static Guid _guidMax = new Guid("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");

	public unsafe Guid DeserializeGuid(ReadOnlySpan<byte> buffer, ref int pos)
	{
		var glyph = buffer[pos++];
		if (glyph == 0)
		{
			return default;
		}
		else if (glyph == 1)
		{
			return _guidMax;
		}
		else if (glyph == 2) // fallback 17 bytes uncompressed (Legacy guids)
		{
			var r = default(Guid);
			byte* bytes = (byte*)&r;
			for (int i = 0; i < 16; i++)
			{
				bytes[i] = buffer[pos++];
			}
			return r;
		}
		else if (glyph >= 4 && glyph <= 7) // v7 compressed [Glyph contains 2 more bits, the high bits of rand_a, 6 more bits are known (ver+var), that is 8, entire byte is off. 6 bytes is diff varint encoded, 9 bytes left]
		{
			// 2 bits are already there in glyph

			var diff = DeserializeSigned(buffer, ref pos);
			var ms = (_streamBaseTime.AddMilliseconds(diff).Ticks - GuidExtensions.UnixEpochTicks) / 10000;

			var r = default(Guid);
			byte* bytes = (byte*)&r;
			uint* ints = (uint*)&r;
			ushort* shorts = (ushort*)&r;

			// Apply Time
			shorts[2] = unchecked((ushort)ms);
			ints[0] = (uint)(ms >> 16);

			// Apply Random_a_low
			bytes[6] = buffer[pos++];

			// Apply Packed
			var packed8 = buffer[pos++];
			bytes[8] = (byte)(((packed8 & 0x3F) | 0b_1000_0000)); // set variant to RFC, and keep 2 bits of rand_a_high
			bytes[7] = (byte)(7 << 4 | (packed8 & 0xC0) >> 4 | glyph & 0b11); // v7 + rand_a_high 2 bits from packed8 + 2 bits from glyph

			// Apply Remaining 7 bytes
			for (int i = 9; i < 16; i++)
			{
				bytes[i] = buffer[pos++];
			}
			return r;
		}
		else if (glyph >= 0b_0010_0000 && glyph <= 0b_0011_1111) // v8 bit presence bit compressed
		{
			// var diff = DeserializeSigned(buffer, ref pos);

			var r = default(Guid);
			byte* bytes = (byte*)&r;
			uint* uints = (uint*)&r;
			ushort* ushorts = (ushort*)&r;
			if ((glyph & 0b00010000) > 0) // #4
			{
				MemoryMarshal.TryRead(buffer[pos..], out uints[0]);
				pos += 4;
			}
			if ((glyph & 0b00001000) > 0) // #3
			{
				MemoryMarshal.TryRead(buffer[pos..], out ushorts[2]);
				pos += 2;
			}
			if ((glyph & 0b00000100) > 0) // #2
			{
				MemoryMarshal.TryRead(buffer[pos..], out ushorts[3]);
				pos += 2;
			}
			if ((glyph & 0b00000010) > 0) // #1
			{
				MemoryMarshal.TryRead(buffer[pos..], out ushorts[4]);
				pos += 2;
			}
			if ((glyph & 0b00000001) > 0) // #0
			{
				MemoryMarshal.TryRead(buffer[pos..], out ushorts[5]); // note extra flip here! for performance reasons
				pos += 2;
				MemoryMarshal.TryRead(buffer[pos..], out bytes[12]); // note extra flip here! for performance reasons
				pos += 1;
				MemoryMarshal.TryRead(buffer[pos..], out bytes[13]); // note extra flip here! for performance reasons
				pos += 1;
				MemoryMarshal.TryRead(buffer[pos..], out bytes[14]); // note extra flip here! for performance reasons
				pos += 1;
				MemoryMarshal.TryRead(buffer[pos..], out bytes[15]); // note extra flip here! for performance reasons
				pos += 1;
			}

			bytes[8] |= 1 << 7; // set variant to RFC
			bytes[7] |= 8 << 4; // set version

			return r;
		}
		else
		{
			var r = default(Guid);
			byte* bytes = (byte*)&r;
			bytes[8] = glyph;
			for (int i = 0; i < 8; i++)
			{
				bytes[i] = buffer[pos++];
			}
			for (int i = 9; i < 16; i++)
			{
				bytes[i] = buffer[pos++];
			}
			return r;
		}
	}

}

