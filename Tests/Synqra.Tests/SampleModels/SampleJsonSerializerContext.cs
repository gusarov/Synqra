using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using SharpCompress.Common;
using Synqra.Tests.BinarySerialization;
using Synqra.Tests.SampleModels.Binding;
using Synqra.Tests.SampleModels.Serialization;
using Synqra.Tests.SampleModels.Syncronization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Synqra.Tests.SampleModels;

[JsonSourceGenerationOptions(
	  JsonSerializerDefaults.Web
	, AllowTrailingCommas = true
	, DefaultBufferSize = 16384
	, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	, DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase
	, GenerationMode = JsonSourceGenerationMode.Default
	, IgnoreReadOnlyFields = true
	, IgnoreReadOnlyProperties = true
	, IncludeFields = false
	, PropertyNameCaseInsensitive = true
	, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
	, ReadCommentHandling = JsonCommentHandling.Skip
	, Converters = [
		typeof(ObjectConverter),
		// typeof(BindableModelConverter),
	]
	// , TypeInfoResolver = new TodoPolymorphicTypeResolver()
#if DEBUG
	, WriteIndented = true
#endif
)]
[JsonSerializable(typeof(DemoModel))]
[JsonSerializable(typeof(SampleOnePropertyObject))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(ISynqraCommand))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(MyPocoTask))]
[JsonSerializable(typeof(SampleTaskModel))]
[JsonSerializable(typeof(TestItem))]
[JsonSerializable(typeof(SampleTodoTask))]

[JsonSerializable(typeof(Synqra.Event))]
[JsonSerializable(typeof(Synqra.CommandCreatedEvent))]
[JsonSerializable(typeof(Synqra.ObjectCreatedEvent))]
[JsonSerializable(typeof(Synqra.ObjectPropertyChangedEvent))]

[JsonSerializable(typeof(Synqra.Command))]
[JsonSerializable(typeof(Synqra.CreateObjectCommand))]
[JsonSerializable(typeof(Synqra.ChangeObjectPropertyCommand))]

[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Int64))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(object))]

[JsonSerializable(typeof(SampleTestData))]
[JsonSerializable(typeof(TransportOperation))]

[JsonSerializable(typeof(SampleTodoTask))]
[JsonSerializable(typeof(SampleFieldIntModel))]
[JsonSerializable(typeof(SampleFieldObjectModel))]
[JsonSerializable(typeof(SampleFieldBaseModel))]
[JsonSerializable(typeof(SampleFieldDerrivedModel))]
[JsonSerializable(typeof(SampleFieldSealedDerivedModel))]
[JsonSerializable(typeof(SampleFieldSealedModel))]
[JsonSerializable(typeof(SampleBaseModel))]
[JsonSerializable(typeof(SampleDerivedModel))]
[JsonSerializable(typeof(SampleSealedDerivedModel))]
[JsonSerializable(typeof(SampleSealedModel))]

[JsonSerializable(typeof(SampleFieldListIntModel))]
[JsonSerializable(typeof(SampleFieldListObjectModel))]
[JsonSerializable(typeof(SampleFieldListBaseModel))]
[JsonSerializable(typeof(SampleFieldListSealedModel))]
[JsonSerializable(typeof(SampleFieldListBaseModel_))]
[JsonSerializable(typeof(SampleFieldEnumerableBaseModel))]
[JsonSerializable(typeof(SampleFieldEnumerableBaseModel_))]

[JsonSerializable(typeof(List<SampleBaseModel>))]
[JsonSerializable(typeof(List<SampleDerivedModel>))]
[JsonSerializable(typeof(List<SampleSealedModel>))]
[JsonSerializable(typeof(List<SampleFieldDictionaryStringObjectModel>))]

// [JsonConverter(typeof(ObjectConverter))]
public partial class SampleJsonSerializerContext : JsonSerializerContext
{
	static Type[] _extra =
	[
		typeof(SamplePublicModel),
		typeof(SampleTaskModel),
	];

	public static JsonSerializerOptions DefaultOptions { get; }

	static SampleJsonSerializerContext()
	{
		DefaultOptions = new JsonSerializerOptions(Default.Options)
		{
			Converters =
			{
				new ObjectConverter(_extra),
			},
			// TypeInfoResolver = new SynqraPolymorphicTypeResolver(_extra),
			/*
			TypeInfoResolver = new DefaultJsonTypeInfoResolver
			{
				Modifiers =
				{
					ti =>
					{
						if (ti.Type == typeof(object))
						{
							ti.PolymorphismOptions = new JsonPolymorphismOptions
							{
								TypeDiscriminatorPropertyName = "_t",
								IgnoreUnrecognizedTypeDiscriminators = true,
								UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
							};
							foreach (var item in _extra)
							{
								ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(item, item.Name));
							}
							// Register supported runtime types (add as many as you like)
							// ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(Uri),          "uri"));
							// ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(DateTime),     "dt"));
							// ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(DateTime),     "dt"));
							// ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(MyMessageA),   "A"));
							// ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(MyMessageB),   "B"));
							// …scan assemblies and add more if desired (see below)
						}
					}
				}
			},*/
		};
	}

	/*
	public override global::System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(global::System.Type type)
	{
		Options.TryGetTypeInfo(type, out global::System.Text.Json.Serialization.Metadata.JsonTypeInfo? typeInfo);
		if (typeInfo.Type == typeof(IBindableModel))
		{
			typeInfo.PolymorphismOptions ??= new JsonPolymorphismOptions
			{
				TypeDiscriminatorPropertyName = "_t",
			};
			typeInfo.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(SampleTaskModel), "SampleTaskModel"));
		}
		return typeInfo;
	}

	// This partial is discovered by the generator; you implement it.
	static partial void ModifyJsonTypeInfo(JsonTypeInfo ti)
	{
		if (ti.Type == typeof(IBindableModel))
		{
			ti.PolymorphismOptions ??= new JsonPolymorphismOptions
			{
				TypeDiscriminatorPropertyName = "_t",
			};
			ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(SampleTaskModel), "SampleTaskModel"));
		}
	}
	*/
}
