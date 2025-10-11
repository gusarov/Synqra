using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http.Headers;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace Synqra.BinarySerializer;

public class SBXSerializer : ISBXSerializer
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
		List = -6,
		// EndOfObject = -7,
	}

	static UTF8Encoding _utf8 = new(false, true)
	{
		// EncoderFallback = EncoderFallback.ExceptionFallback,
		// DecoderFallback = DecoderFallback.ExceptionFallback,
	};

	// This is to compress time in streams. All time can now be calculatead as varbinary of this custom epoch (when stream started). This is to save space.
	DateTime _streamBaseTime = DateTime.UtcNow;
	Dictionary<int, (float schemaVersion, Type type)> _typeById = new();
	Dictionary<Type, (int typeId, float schemaVersion)> _idByType = new();

	public void SetTimeBase(DateTime streamBaseTime)
	{
		if (streamBaseTime.Kind != DateTimeKind.Utc)
		{
			throw new ArgumentException("Stream base time must be in UTC.");
		}
		_streamBaseTime = streamBaseTime;
	}

	public void Map(int typeId, double schemaVersion, Type type)
	{
		var schemaVersionF = (float)schemaVersion;
		_typeById[typeId] = (schemaVersionF, type);
		_idByType[type] = (typeId, schemaVersionF);
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

	public void Serialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(in Span<byte> buffer, T? obj, ref int pos, bool? emitTypeId = null)
	{
		Serialize(buffer, obj, ref pos, typeof(T), emitTypeId);
	}

	public void Serialize(
		  in Span<byte> buffer
		, object? obj
		, ref int pos
		, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type requestedType
		, bool? emitTypeId = null
		)
	{
		if (obj == null)
		{
			throw new Exception("Decision about null serialization is not yet finalized");
		}

		// If this is level 1 (precise custom) then schema id. Schema ID always assumes a class name (type).
		// If this is level 2 (field names) then 0 (schemaid=0) and the first field is the type name string.
		// If this is level 3, the reset of the fields are in alphabetical order by field name (to ensure canonical normalization for auto-repairs & hash tree)

		var type = obj?.GetType() ?? throw new ArgumentNullException();
		if (emitTypeId == null)
		{
			if (requestedType.IsValueType || requestedType.IsSealed)
			{
				emitTypeId = false;
			}
		}

		if (emitTypeId != false)
		{
			if (requestedType != type)
			{
				var typeId = GetTypeId(type);
				Serialize(in buffer, (int)typeId, ref pos);
				if (typeId == 0)
				{
					var aqn = type.AssemblyQualifiedName;
					aqn = aqn[0..aqn.IndexOf(',', aqn.IndexOf(',') + 1)]; // trim assembly part

					Serialize(buffer, aqn ?? throw new NotSupportedException(), ref pos);
				}
			}
			else
			{
				Serialize(in buffer, (int)TypeId.AsRequested, ref pos);
			}
		}

		if (obj is IBindableModel bm)
		{
			_idByType.TryGetValue(type, out var typeInfo);
			EmergencyLog.Default.Debug($"Syncron Serializing {type.FullName} with schema version {typeInfo.schemaVersion}");
			bm.Get(this, typeInfo.schemaVersion, buffer, ref pos);
		}
		else if (obj is int i)
		{
			Serialize(in buffer, (long)i, ref pos);
		}
		else if (obj is long l)
		{
			Serialize(in buffer, (long)l, ref pos);
		}
		else if (obj is uint ui)
		{
			Serialize(in buffer, (ulong)ui, ref pos);
		}
		else if (obj is ulong ul)
		{
			Serialize(in buffer, (ulong)ul, ref pos);
		}
		else if (obj is Guid g)
		{
			Serialize(in buffer, g, ref pos);
		}
		else if (obj is string s)
		{
			Serialize(in buffer, s, ref pos);
		}
		else if (obj is IEnumerable enumerable)
		{
			Serialize(in buffer, enumerable, ref pos, requestedType);
		}
		else
		{
			// Only generated bindable models are supported for Level 1 & 2 serialization. All other types are Level 3 (field names).
			var props = type.GetProperties();
			foreach (var item in props)
			{
				var val = item.GetValue(obj);
				if (val != null)
				{
					Serialize(in buffer, item.Name, ref pos);
					Serialize(in buffer, val, ref pos);
				}
			}
			// we keep reading strings for the next property name! So let empty string be EndOfObject
			Serialize(in buffer, "", ref pos);
		}
	}

	public T Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
		  in ReadOnlySpan<byte> buffer
		, ref int pos
		, bool? consumeTypeId = null
		)
	{
		return (T)Deserialize(
			  buffer
			, ref pos
			, requestedType: typeof(T)
			, consumeTypeId
			);
	}

	public object Deserialize(
		  in ReadOnlySpan<byte> buffer
		, ref int pos
		, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type? requestedType = null
		, bool? consumeTypeId = null
		)
	{
		if (requestedType == null)
		{
			requestedType = typeof(object);
		}
		if (consumeTypeId == null)
		{
			consumeTypeId = !requestedType.IsValueType && !requestedType.IsSealed;
		}
		if (consumeTypeId != true && requestedType == typeof(object))
		{
			throw new InvalidOperationException("Neither consumeTypeId nor requestType provided.");
		}

		// read type
		var typeId = consumeTypeId == true
			? (TypeId)DeserializeSigned(in buffer, ref pos)
			: GetTypeId(requestedType ?? throw new ArgumentNullException(nameof(requestedType), "For the case when consumeTypeId == false"))
			;

		Type type;
		object value;
		float schemaVersion;
		if (typeId == 0)
		{
			type = Type.GetType(DeserializeString(in buffer, ref pos), true);
			schemaVersion = default;
		}
		else if (typeId == TypeId.AsRequested)
		{
			type = requestedType;
			_idByType.TryGetValue(type, out var typeInfo);
			schemaVersion = typeInfo.schemaVersion;
		}
		else
		{
			type = GetTypeFromId(typeId);
			schemaVersion = 0;
		}

		if (typeId == TypeId.SignedInteger || type == typeof(int) || type == typeof(long))
		{
			var res = DeserializeSigned(in buffer, ref pos);
			if (requestedType != null && requestedType != res.GetType())
			{
				return Convert.ChangeType(res, requestedType);
			}
			return res;
		}
		else if (typeId == TypeId.UnsignedInteger || type == typeof(uint) || type == typeof(ulong))
		{
			var res = DeserializeUnsigned(in buffer, ref pos);
			if (requestedType != null && requestedType != res.GetType())
			{
				return Convert.ChangeType(res, requestedType);
			}
			return res;

		}
		else if (typeId == TypeId.Utf8String || type == typeof(string))
		{
			return DeserializeString(in buffer, ref pos);
		}
		else if (type.IsAssignableTo(typeof(IEnumerable)))
		{
			return DeserializeList(in buffer, ref pos, requestedCollectionType: type, consumeTypeId: false);
		}
		else if (typeId == TypeId.Guid || type == typeof(Guid))
		{
			return DeserializeGuid(in buffer, ref pos);
		}
		else if (typeId == TypeId.List || type.IsAssignableTo(typeof(IEnumerable)))
		{
			type = requestedType;
			if (type.IsAssignableTo(typeof(IEnumerable)))
			{
				return DeserializeList(in buffer, ref pos, requestedCollectionType: type, consumeTypeId: false);
			}
			throw new Exception();
		}
		else if (_typeById.TryGetValue((int)typeId, out var typeInfo))
		{
			type = typeInfo.type;
			schemaVersion = typeInfo.schemaVersion;
		}

		if (type.IsPrimitive)
		{
			throw new Exception("Primitives supposed to be handled by now");
		}
		value = Activator.CreateInstance(type) ?? throw new Exception("Could not create instance of type " + type.FullName);
		if (schemaVersion != 0)
		{
			if (value is IBindableModel bm)
			{
				bm.Set(this, schemaVersion, in buffer, ref pos);
				return value; // do not process named properties for now
			}
			else
			{
				throw new NotSupportedException("Only IBindableModel are supported for schema versioning.");
			}
		}

		// free key-value pairs
		while (true)
		{
			// keep reading properties as strings
			var propName = DeserializeString(in buffer, ref pos);
			if (propName.StartsWith("//"))
			{
				break; // drop the comment at the end of buffer
			}
			if (propName == "")
			{
				break; // stop on empty property name
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
			var propValue = Deserialize(in buffer, ref pos);
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

	#region List

	public void Serialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
		  in Span<byte> buffer
		, in IEnumerable<T> list
		, ref int pos
		)
	{
		Serialize(
			  buffer
			, list
			, ref pos
			, typeof(T)
			);
	}

	public void Serialize(
		  in Span<byte> buffer
		, in IEnumerable enumerable
		, ref int pos
		, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type? requestedCollectionType = null
		)
	{
		// before touching actual element and collections, we need to reliably asses static requested type - do we even need type_id or not?
		// we do not need type id:
		// 1) it is collection of value types (no covariance)
		// 2) it is collection of sealed reference types (disabled covariance)
		// we do need type id:
		// 1) it is object or collection of non sealed reference types (any element in between might be of different type now or later)
		// 2) requested collection type is not specified - same as object

		if (requestedCollectionType == null)
		{
			requestedCollectionType = typeof(object);
		}
		var enumerableType = enumerable.GetType();
		if (!requestedCollectionType.IsAssignableFrom(enumerableType))
		{
			throw new ArgumentException($"Requested collection type {requestedCollectionType.Name} is not assignable from this collection {enumerableType.Name}");
		}

		bool isArray;
		Type requestedElementType;
		if (requestedCollectionType.IsArray)
		{
			isArray = true;
			requestedElementType = requestedCollectionType.GetElementType() ?? throw new ArgumentException("Could not get element type from array type " + requestedCollectionType.Name);
		}
		else
		{
			isArray = false;
			var requestedGenericEnumerable = enumerable.GetType().GetInterfaces().SingleOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>)); // TODO: There are rare cases with multiple different IEnumerable<T>
			if (requestedGenericEnumerable != null)
			{
				requestedElementType = requestedGenericEnumerable.GetGenericArguments()[0];
			}
			else
			{
				requestedElementType = typeof(object);
			}
		}

		bool isBuiltTimeImpliedType;
		if (requestedElementType.IsValueType || requestedElementType.IsSealed)
		{
			// we do not need any type id at all, and it is statically proven, both elements and the list itself will be of requestedElementType
			isBuiltTimeImpliedType = true;
		}
		else
		{
			// we need type id, and now we can judge
			isBuiltTimeImpliedType = false;
		}

		var materialized = enumerable as ICollection ?? enumerable.Cast<object?>().ToArray();

		var monogenicList = true;
		if (!isBuiltTimeImpliedType)
		{
			Type? monogenicType = null;
			foreach (var item in materialized ?? throw new Exception("This will never happen"))
			{
				var itemType = item.GetType();
				if (monogenicType == null)
				{
					monogenicType = itemType;
				}
				else if (monogenicType != itemType)
				{
					monogenicList = false;
					break;
				}
			}
		}

		if (isBuiltTimeImpliedType) // no need in type id
		{

		}
		else if (monogenicList)
		{
			// 1. all items are of the same type - then we can write type once, and then all items.
		}
		else
		{
			// 2. items are of different types - then we have to write type for each item, and this mean we can easily zero-end collections of different types
		}
		Serialize(buffer, (uint)materialized.Count, ref pos);

		foreach (var item in materialized)
		{
			Serialize(buffer, item, ref pos, emitTypeId: !isBuiltTimeImpliedType);
		}
	}

	public IList<T> DeserializeList<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
		  in ReadOnlySpan<byte> buffer
		, ref int pos
	)
	{
		var list = new List<T>();
		return (IList<T>)DeserializeList(
			  in buffer
			, ref pos
			, listToFill: list
			, requestedElementType: typeof(T)
			);
	}

	public IList<T> DeserializeList<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
		  in ReadOnlySpan<byte> buffer
		, ref int pos
		, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type? requestedCollectionType = null
		, bool? consumeTypeId = null
		)
	{
		var list = new List<T>();
		return (IList<T>)DeserializeList(
			  in buffer
			, ref pos
			, listToFill: list
			, requestedCollectionType: requestedCollectionType
			, requestedElementType: typeof(T)
			, consumeTypeId: consumeTypeId
			);
	}

	public IList DeserializeList(
		  in ReadOnlySpan<byte> buffer
		, ref int pos
		, IList? listToFill = null
		, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type? requestedCollectionType = null
		, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type? requestedElementType = null
		, bool? consumeTypeId = null
		)
	{
		var count = (int)DeserializeUnsigned(buffer, ref pos);

		if (listToFill == null)
		{
			if (requestedCollectionType == null && requestedElementType == null)
			{
				throw new ArgumentException("Requested element type must be provided for deserialization into IList");
			}

			var requestedGenericEnumerable = requestedCollectionType.IsGenericType && requestedCollectionType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
				? requestedCollectionType
				: requestedCollectionType.GetInterfaces().SingleOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>)); // TODO: There are rare cases with multiple different IEnumerable<T>
			requestedElementType = requestedGenericEnumerable.GetGenericArguments()[0];
			if (requestedCollectionType.IsAbstract || requestedCollectionType.IsInterface || requestedCollectionType.IsGenericTypeDefinition)
			{
				requestedCollectionType = null;
			}
			else
			{

			}

			listToFill = (IList)Activator.CreateInstance(requestedCollectionType ?? typeof(List<>).MakeGenericType(requestedElementType), count) ?? throw new Exception("Could not create instance of list of " + requestedElementType.Name);
		}
		for (int i = 0; i < count; i++)
		{
			var item = Deserialize(buffer, ref pos, requestedElementType, consumeTypeId: false);
			listToFill.Add(item);
		}
		return listToFill;
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
			case Type t when t == typeof(Guid):
				return TypeId.Guid;
			case Type t when t.IsAssignableTo(typeof(IEnumerable)):
				return TypeId.List;
			default:
				_idByType.TryGetValue(type, out var metaData); // it will stay Unknown if there is no such type, as designed.
				return (TypeId)metaData.typeId;
				// throw new Exception($"Type {type.FullName} is not supported for serialization.");
		}
	}

	private Type GetTypeFromId(TypeId typeId)
	{
		if (_typeById.TryGetValue((int)typeId, out var metaData))
		{
			return metaData.type;
		}
		return typeId switch
		{
			TypeId.SignedInteger => typeof(long),
			TypeId.UnsignedInteger => typeof(ulong),
			TypeId.Utf8String => typeof(string),
			TypeId.Guid => typeof(Guid),
			_ => throw new Exception($"TypeId {typeId} is not supported for deserialization."),
		};
	}

	public void Serialize(in Span<byte> buffer, Type type, ref int pos)
	{
		var typeId = GetTypeId(type);
		Serialize(buffer, typeId, ref pos);
		if (typeId == TypeId.Unknown)
		{
			var typeName = type.FullName ?? throw new Exception("Type has no full name: " + type.Name);
			Serialize(buffer, typeName, ref pos);
		}
	}

	public Type Deserialize(in Span<byte> buffer, ref int pos)
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

	public string DeserializeString(in ReadOnlySpan<byte> buffer, ref int pos)
	{
		int length = buffer[pos..].IndexOf<byte>(0); // todo: it probably allocates array here :(
		if (length < 0)
		{
			throw new ArgumentException("Buffer too small for string decoding.");
		}

		var str = _utf8.GetString(buffer[pos..][..length]);
		pos += length + 1;
		return str;
	}

	public string DeserializeString(ref ReadOnlySpan<byte> buffer) // 37 38 00
	{
		int length = buffer.IndexOf<byte>(0); // todo: it probably allocates array here :(
		// var length = (int)DeserializeUnsigned(ref buffer);
		if (length < 0)
		{
			throw new ArgumentException("Buffer too small for string decoding.");
		}
		var str = _utf8.GetString(buffer[0..length]);
		buffer = buffer[(length + 1)..];
		return str;
	}

	public void Serialize(in Span<byte> buffer, string data, ref int pos)
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

	public static int IndexOfNull(in ReadOnlySpan<byte> span)
	{
		// TODO: it probably allocates array here :( need more optimization
		return Array.IndexOf(span.ToArray(), (byte)0);
	}

	#endregion

	#region Signed Integer

	private void Serialize(in Span<byte> buffer, in TypeId data, ref int pos)
	{
		Serialize(buffer, (int)data, ref pos);
	}

	public void Serialize(in Span<byte> buffer, in long data, ref int pos)
	{
		// ZigZag encode the signed int
		ulong zigzag = (ulong)(data << 1 ^ data >> 63);
		while (zigzag >= 0x80UL)
		{
			if (pos >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint encoding.");
			buffer[pos++] = (byte)(zigzag & 0x7F | 0x80);
			zigzag >>= 7;
		}
		if (pos >= buffer.Length)
			throw new ArgumentException("Buffer too small for varint encoding.");
		buffer[pos++] = (byte)zigzag;
	}

	public void Serialize(ref Span<byte> buffer, in long data)
	{
		// ZigZag encode the signed int
		ulong zigzag = (ulong)(data << 1 ^ data >> 63);
		int count = 0;
		while (zigzag >= 0x80UL)
		{
			if (count >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint encoding.");
			buffer[count++] = (byte)(zigzag & 0x7FUL | 0x80UL);
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

	public long DeserializeSigned(in ReadOnlySpan<byte> buffer, ref int pos)
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

	public void Serialize(in Span<byte> buffer, ulong value, ref int pos)
	{
		// Protobuf-style varint encoding for uint32 (little-endian, 7 bits per byte, MSB=1 means more)
		while (value >= 0x80)
		{
			if (pos >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint encoding.");
			buffer[pos++] = (byte)(value & 0x7F | 0x80);
			value >>= 7;
		}
		if (pos >= buffer.Length)
			throw new ArgumentException("Buffer too small for varint encoding.");
		buffer[pos++] = (byte)value;
	}

	public void Serialize(ref Span<byte> buffer, ulong value)
	{
		// Protobuf-style varint encoding for uint32 (little-endian, 7 bits per byte, MSB=1 means more)
		int count = 0;
		while (value >= 0x80)
		{
			if (count >= buffer.Length)
				throw new ArgumentException("Buffer too small for varint encoding.");
			buffer[count++] = (byte)(value & 0x7F | 0x80);
			value >>= 7;
		}
		if (count >= buffer.Length)
			throw new ArgumentException("Buffer too small for varint encoding.");
		buffer[count++] = (byte)value;
		buffer = buffer[count..];
	}

	public ulong DeserializeUnsigned(in ReadOnlySpan<byte> buffer, ref int pos)
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

	public unsafe void Serialize(in Span<byte> buffer, Guid data, ref int pos)
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
						buffer[pos++] = (byte)(1<<2 | bytes[7] & 0x03); // 4-7: UUIDv7 - time based + 2 bits from rand_a_high
						var ms = data.GetTimestamp();
						var tsdiff = checked((int)(ms - _streamBaseTime).TotalMilliseconds);
						Serialize(buffer, tsdiff, ref pos);
						buffer[pos++] = bytes[6]; // rand_a_low
						buffer[pos++] = (byte)((bytes[7] & 0x0C)<<4 | bytes[8] & 0x3F); // packed8 rand_a_high 2 other bit + byte8
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

	public unsafe Guid DeserializeGuid(in ReadOnlySpan<byte> buffer, ref int pos)
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
			bytes[8] = (byte)(packed8 & 0x3F | 0b_1000_0000); // set variant to RFC, and keep 2 bits of rand_a_high
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

