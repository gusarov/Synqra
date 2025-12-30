using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Synqra;

public class BindableModelConverter : JsonConverter<IBindableModel>
{
	private readonly Type[] _extraTypes;

	public BindableModelConverter(params Type[] extraTypes)
	{
		_extraTypes = extraTypes;
	}

	public override IBindableModel? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
	{
		if (options.TypeInfoResolver == null)
		{
			throw new SynqraJsonException();
		}
		throw new SynqraJsonException();
	}

	public override void Write(Utf8JsonWriter writer, IBindableModel value, JsonSerializerOptions options)
	{
		if (options.TypeInfoResolver == null)
		{
			throw new SynqraJsonException();
		}
		throw new SynqraJsonException();
	}
}


public class ObjectConverter : JsonConverter<object>
{
	private readonly Type[] _extraTypes;

	public ObjectConverter(params Type[] extraTypes)
	{
		_extraTypes = extraTypes;
		// Microsoft SerializeAsync implementation is very hacky with their custom ObjectConverter. It is not possible to use it as is. As soon as our converter injected, it will serialize IAsyncEnumerable as a value unless I set this internal property.
		// GetType().GetProperty("CanBePolymorphic", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(this, true);
	}

	public override bool CanConvert(Type typeToConvert)
	{
		return base.CanConvert(typeToConvert);
	}

	public override object? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
	{
		object? str(string str)
		{
			// This might seem as partially duplicated with TodoDateTimeConverter, but the difference is:
			// TodoDateTimeConverter requires value to end up a DateTime, so more strict validation
			// ObjectConverter only attempts to figure out if it is date time, e.g. "subject" is not datetime and there is less validation, fallback to string

			if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var datetime))
			{
				// this mean it supposed to be string, but is it valid enough to be parsed as DateTime in our system?

				if (datetime.Kind != DateTimeKind.Utc)
				{
					throw new SynqraJsonException($"The json representation supposed to have UTC time, but was parsed as {datetime.Kind}: {str} => {datetime}");
				}
				if (!str.EndsWith("Z"))
				{
					throw new SynqraJsonException($"The json representation supposed to have UTC time, ending with Z, not {str}");
				}
				return datetime;
			}
			return str;
		}

		switch (reader.TokenType)
		{
			case JsonTokenType.True: return true;
			case JsonTokenType.False: return false;
			case JsonTokenType.Number when reader.TryGetInt32(out int n): return n;
			case JsonTokenType.Number when reader.TryGetInt64(out long n): return n;
			case JsonTokenType.Number when reader.TryGetUInt64(out ulong n): return n;
			case JsonTokenType.Number: return reader.GetDouble();
			// case // JsonTokenType.String when reader.TryGetDateTime(out DateTime datetime): return datetime,
			// case // JsonTokenType.String: return str(reader.GetString()),
			case JsonTokenType.String: return str(reader.GetString());
			case JsonTokenType.StartObject:
			{
				var original = reader;
				if (!reader.Read()) throw new SynqraJsonException("Can't advance 1");
				// consume _t property if present, and then deserialize as that type
				if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == "_t")
				{
					if (!reader.Read()) throw new SynqraJsonException("Can't advance 2");
					var typeName = reader.GetString();
					Type? derivedType = null;
					foreach (var rootType in SynqraPolymorphicTypeResolver.PolimorficRoots)
					{
						var attrs = (JsonDerivedTypeAttribute[])rootType.GetCustomAttributes(typeof(JsonDerivedTypeAttribute), true);
						foreach (var attr in attrs)
						{
							if (attr.DerivedType.Name == typeName)
							{
								derivedType = attr.DerivedType;
								break;
							}
						}
						if (derivedType != null)
						{
							break;
						}
					}
					if (derivedType == null)
					{
						foreach (var item in _extraTypes)
						{
							if (item.Name == typeName)
							{
								derivedType = item;
								break;
							}
						}
					}
					if (derivedType != null)
					{
						var res = JsonSerializer.Deserialize(ref original, derivedType, options);
						reader = original;
						return res;
					}
					else
					{
						throw new SynqraJsonException($"Unknown derived type name: {typeName}");
					}
				}
				break;
			}
		};

		throw new SynqraJsonException("Reader did not found a way to deserialzie this json");
	}

	public override void Write(Utf8JsonWriter w, object value, JsonSerializerOptions options)
	{
		var type = value.GetType();
		if (type == typeof(string) || type.IsPrimitive || value is JsonElement)
		{
			JsonSerializer.Serialize(w, value, type, options);
			return;
		}

		Type? rootType = type;
		foreach (var ancestor in type.GetAncestors())
		{
			if (SynqraPolymorphicTypeResolver.PolimorficRoots.Contains(ancestor))
			{
				rootType = ancestor;
				/*
				if (ancestor != typeof(IBindableModel))
				{
					if (options.TypeInfoResolver == null)
					{
						throw new SynqraJsonException();
					}
					options = new JsonSerializerOptions(options)
					{
						TypeInfoResolver = new SynqraPolymorphicTypeResolver(_extraTypes),
					};
				}
				*/
			}
		}

		if (value is IAsyncStateMachine) // this branch is disabled in favour of CanBePolymorphic flag. Both are - to support IAsyncEnumerable
		{
			throw new SynqraJsonException("IAsyncStateMachine is temporarely disabled");
			w.Flush();
			var pipeWriter = (PipeWriter)w.GetType().GetField("_output", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(w);
			if (pipeWriter is null)
			{
				throw new SynqraJsonException("PipeWriter is null");
			}
			JsonSerializer.SerializeAsync(pipeWriter, value, rootType, options, default).Wait();
		}
		else
		{
			var typeName = type?.Name;
			EmergencyLog.Default.Debug($"ObjectConverter.JsonSerializer.Serialize: {typeName}");
			if (rootType == type)
			{
				w.WriteStartObject();
				w.WriteString("_t", typeName);

				var elem = JsonSerializer.SerializeToElement(value, type, options);
				foreach (var p in elem.EnumerateObject())
				{
					if (!string.Equals(p.Name, "_t", StringComparison.Ordinal))
					{
						p.WriteTo(w);
					}
				}

				w.WriteEndObject();
			}
			else
			{
				JsonSerializer.Serialize(w, value, rootType, options);
			}
			/*
			var optionsNoObjectConverter = new JsonSerializerOptions(options);
			var q = optionsNoObjectConverter.Converters.Remove(this);
			// JsonSerializer.Serialize(w, value, rootType, optionsNoObjectConverter);
			JsonSerializer.Serialize(w, value, inputType: typeof(object), optionsNoObjectConverter);
			*/
		}
	}
}

public static class TypeExtensions
{
	public static IEnumerable<Type> GetAncestors(this Type type)
	{
		if (type == null) yield break;
		yield return type;
		if (type.BaseType != null)
		{
			foreach (var ancestor in type.BaseType.GetAncestors())
			{
				yield return ancestor;
			}
		}
	}
}

/// <summary>
/// Enables _t Polymorfism for Conmmand
/// Enables _t Polymorfism for IComponent
/// </summary>
public class SynqraPolymorphicTypeResolver : DefaultJsonTypeInfoResolver
{
	// private static int _lastComponentsCount;
	// private static JsonPolymorphismOptions _lastComponents;
	// private static JsonPolymorphismOptions _lastCommands;

	// mongo attribute driven with name match
	Dictionary<Type, List<JsonDerivedType>> _derivedTypes = new Dictionary<Type, List<JsonDerivedType>>();

	public SynqraPolymorphicTypeResolver(params Type[] extraTypes)
	{
		_extraTypes = extraTypes;
	}

	public static HashSet<Type> PolimorficRoots = new HashSet<Type>
	{
		typeof(Event),
		typeof(Command),
		typeof(TransportOperation),
		typeof(IBindableModel),
	};
	private readonly Type[] _extraTypes;

	public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
	{
		// lock (this)
		{
			var jsonTypeInfo = base.GetTypeInfo(type, options);
			/*
			if (type == typeof(IComponent))
			{
				// cache it but update if new components registered. There is no way to undergister in runtime
				// if (_lastComponents is null || _lastComponentsCount != ComponentRegister.Instance.Components.Count)
				if (jsonTypeInfo.PolymorphismOptions == null)
				{
					var poliOptions = new JsonPolymorphismOptions
					{
						TypeDiscriminatorPropertyName = "_t",
						IgnoreUnrecognizedTypeDiscriminators = false,
						UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
					};
					foreach (var item in ComponentRegister.Instance.Components.Select(x => new JsonDerivedType(x.Type, x.Discriminator)))
					{
						poliOptions.DerivedTypes.Add(item);
					}
					// _lastComponents = poliOptions;
					jsonTypeInfo.PolymorphismOptions = poliOptions; // _lastComponents;
				}
			}
			else if (type == typeof(IStreamable))
			{
				if (jsonTypeInfo.PolymorphismOptions == null)
				{
					var poliOptions = new JsonPolymorphismOptions
					{
						TypeDiscriminatorPropertyName = "_t",
						IgnoreUnrecognizedTypeDiscriminators = false,
						UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
					};
					poliOptions.DerivedTypes.Add(new JsonDerivedType(typeof(Todo.Schema.WorldStreamDocumentMarker), "WorldStreamDocumentMarker"));
					poliOptions.DerivedTypes.Add(new JsonDerivedType(typeof(Todo.Schema.CollectionStartMarker), "CollectionStartMarker"));
					poliOptions.DerivedTypes.Add(new JsonDerivedType(typeof(TodoTask), "TodoTask"));
					poliOptions.DerivedTypes.Add(new JsonDerivedType(typeof(TodoNode), "TodoNode"));
					poliOptions.DerivedTypes.Add(new JsonDerivedType(typeof(TodoNodeRelation), "TodoNodeRelation"));
					poliOptions.DerivedTypes.Add(new JsonDerivedType(typeof(TodoDemoMultiParent), "TodoDemoMultiParent"));
					jsonTypeInfo.PolymorphismOptions = poliOptions;
				}
			}
			else*/
			if (PolimorficRoots.Contains(type))
			{
				if (jsonTypeInfo.PolymorphismOptions == null)
				{
#if NET7_0_OR_GREATER
					ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(_derivedTypes, type, out var exists);
					if (!exists)
					{
						list = new List<JsonDerivedType>();
						/*
						var attrs = (BsonKnownTypesAttribute[])type.GetCustomAttributes(typeof(BsonKnownTypesAttribute), true);
						foreach (var attr in attrs)
						{
							foreach (var knownType in attr.KnownTypes)
							{
								list.Add(new JsonDerivedType(knownType, knownType.Name));
							}
						}
						*/
						if (type == typeof(IBindableModel))
						{
							foreach (var type2 in PolimorficRoots)
							{
								var attrs = (JsonDerivedTypeAttribute[])type2.GetCustomAttributes(typeof(JsonDerivedTypeAttribute), true);
								foreach (var attr in attrs)
								{
									list.Add(new JsonDerivedType(attr.DerivedType, attr.DerivedType.Name));
								}
							}
							foreach (var item in _extraTypes)
							{
								list.Add(new JsonDerivedType(item, item.Name));
							}
						}
						else
						{
							var attrs = (JsonDerivedTypeAttribute[])type.GetCustomAttributes(typeof(JsonDerivedTypeAttribute), true);
							foreach (var attr in attrs)
							{
								list.Add(new JsonDerivedType(attr.DerivedType, attr.DerivedType.Name));
							}
						}
					}
					/*
					var poliOptions = new JsonPolymorphismOptions
					{
						TypeDiscriminatorPropertyName = "_t",
						IgnoreUnrecognizedTypeDiscriminators = false,
						UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
					};
					foreach (var knownType in list)
					{
						poliOptions.DerivedTypes.Add(knownType);
					}
					jsonTypeInfo.PolymorphismOptions = poliOptions;
					*/
#endif
				}
			}

			return jsonTypeInfo;
		}
	}
}



[Serializable]
public class SynqraJsonException : Exception
{
	public SynqraJsonException() { }
	public SynqraJsonException(string message) : base(message) { }
	public SynqraJsonException(string message, Exception inner) : base(message, inner) { }
	protected SynqraJsonException(
	  System.Runtime.Serialization.SerializationInfo info,
	  System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
}