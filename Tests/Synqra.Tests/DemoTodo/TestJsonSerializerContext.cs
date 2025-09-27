using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using Synqra.Tests.ModelManagement;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Synqra.Tests.DemoTodo;

[JsonSourceGenerationOptions(
	  AllowTrailingCommas = true
	, DefaultBufferSize = 16384
	, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	, DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase
	, GenerationMode = JsonSourceGenerationMode.Default
	, IgnoreReadOnlyFields = false
	, IgnoreReadOnlyProperties = false
	, IncludeFields = false
	, PropertyNameCaseInsensitive = true
	, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
	, ReadCommentHandling = JsonCommentHandling.Skip
	// , TypeInfoResolver = new TodoPolymorphicTypeResolver()
#if DEBUG
	, WriteIndented = true
#endif
)]
[JsonSerializable(typeof(DemoModel))]
[JsonSerializable(typeof(DemoObject))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(ISynqraCommand))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(MyPocoTask))]
[JsonSerializable(typeof(MyTaskModel))]
[JsonSerializable(typeof(TestItem))]
[JsonSerializable(typeof(TodoTask))]

[JsonSerializable(typeof(Synqra.Event))]
[JsonSerializable(typeof(Synqra.CommandCreatedEvent))]
[JsonSerializable(typeof(Synqra.ObjectCreatedEvent))]
[JsonSerializable(typeof(Synqra.ObjectPropertyChangedEvent))]

[JsonSerializable(typeof(Synqra.Command))]
[JsonSerializable(typeof(Synqra.ChangeObjectPropertyCommand))]
public partial class TestJsonSerializerContext : JsonSerializerContext
{
}

/// <summary>
/// Enables _t Polymorfism for Conmmand
/// Enables _t Polymorfism for IComponent
/// </summary>
public class TodoPolymorphicTypeResolver : DefaultJsonTypeInfoResolver
{
	// private static int _lastComponentsCount;
	// private static JsonPolymorphismOptions _lastComponents;
	// private static JsonPolymorphismOptions _lastCommands;

	// mongo attribute driven with name match
	Dictionary<Type, List<JsonDerivedType>> _derivedTypes = new Dictionary<Type, List<JsonDerivedType>>();

	public TodoPolymorphicTypeResolver()
	{
		Console.WriteLine();
	}

	public static HashSet<Type> PolimorficRoots = new HashSet<Type>
	{
		typeof(Event),
		typeof(Command),
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
			else */ if (PolimorficRoots.Contains(type))
			{
				if (jsonTypeInfo.PolymorphismOptions == null)
				{
					ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(_derivedTypes, type, out var exists);
					if (!exists)
					{
						list = new List<JsonDerivedType>();
						var attrs = (BsonKnownTypesAttribute[])type.GetCustomAttributes(typeof(BsonKnownTypesAttribute), true);
						foreach (var attr in attrs)
						{
							foreach (var knownType in attr.KnownTypes)
							{
								list.Add(new JsonDerivedType(knownType, knownType.Name));
							}
						}
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
				}
			}

			return jsonTypeInfo;
		}
	}
}