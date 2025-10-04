﻿using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Synqra;

public class ObjectConverter : JsonConverter<object>
{
	public ObjectConverter()
	{
		// Microsoft SerializeAsync implementation is very hacky with their custom ObjectConverter. It is not possible to use it as is. As soon as our converter injected, it will serialize IAsyncEnumerable as a value unless I set this internal property.
		// GetType().GetProperty("CanBePolymorphic", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(this, true);
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

		return reader.TokenType switch
		{
			JsonTokenType.True => true,
			JsonTokenType.False => false,
			JsonTokenType.Number when reader.TryGetInt32(out int n) => n,
			JsonTokenType.Number when reader.TryGetInt64(out long n) => n,
			JsonTokenType.Number when reader.TryGetUInt64(out ulong n) => n,
			JsonTokenType.Number => reader.GetDouble(),
			// JsonTokenType.String when reader.TryGetDateTime(out DateTime datetime) => datetime,
			// JsonTokenType.String => str(reader.GetString()),
			JsonTokenType.String => str(reader.GetString()),
			_ => JsonDocument.ParseValue(ref reader).RootElement.Clone()
		};

		// Forward to the JsonElement converter
		var converter = options.GetConverter(typeof(JsonElement)) as JsonConverter<JsonElement>;
		if (converter != null)
		{
			return converter.Read(ref reader, type, options);
		}

		throw new SynqraJsonException();
	}

	public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
	{
		var type = value.GetType();
		Type rootType = type;
		foreach (var ancestor in type.GetAncestors())
		{
			if (SynqraPolymorphicTypeResolver.PolimorficRoots.Contains(ancestor))
			{
				rootType = ancestor;
			}
		}

		if (value is IAsyncStateMachine) // this branch is disabled in favour of CanBePolymorphic flag. Both are - to support IAsyncEnumerable
		{
			throw new SynqraJsonException("IAsyncStateMachine is temporarely disabled");
			writer.Flush();
			var pipeWriter = (PipeWriter)writer.GetType().GetField("_output", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(writer);
			if (pipeWriter is null)
			{
				throw new SynqraJsonException("PipeWriter is null");
			}
			JsonSerializer.SerializeAsync(pipeWriter, value, rootType, options, default).Wait();
		}
		else
		{
			var typeName = value?.GetType()?.Name;
			EmergencyLog.Default.Message($"ObjectConverter.JsonSerializer.Serialize: {typeName}");
			if (typeName?.Contains("Task") == true)
			{
				EmergencyLog.Default.Message($"ObjectConverter.JsonSerializer.Serialize: Stack {new StackTrace()}");
			}
			JsonSerializer.Serialize(writer, value, rootType, options);
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

	public SynqraPolymorphicTypeResolver()
	{

	}

	public static HashSet<Type> PolimorficRoots = new HashSet<Type>
	{
		typeof(Event),
		typeof(Command),
		typeof(TransportOperation),
	};

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
					}
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