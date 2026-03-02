using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using SharpCompress.Common;
using Synqra.Projection.File;
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
	  AllowTrailingCommas = true
	, DefaultBufferSize = 16 * 1024
	, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	, DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase
	, GenerationMode = JsonSourceGenerationMode.Default
	, IgnoreReadOnlyFields = true
	, IgnoreReadOnlyProperties = true
	, IncludeFields = false
	, PropertyNameCaseInsensitive = true
	, ReadCommentHandling = JsonCommentHandling.Skip
	// , TypeInfoResolver = new TodoPolymorphicTypeResolver()
#if DEBUG
	, WriteIndented = true
#endif
	, Converters = [
		typeof(ObjectConverter),
		// typeof(BindableModelConverter),
	]
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
[JsonSerializable(typeof(SampleTodoTaskPoco))]
[JsonSerializable(typeof(Item))] // Synqra.File

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

[JsonSerializable(typeof(SampleTestDataPoco))]
[JsonSerializable(typeof(TransportOperation))]

[JsonSerializable(typeof(SampleTodoTaskPoco))]
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

[JsonConverter(typeof(ObjectConverter))] // re-supplied with extras below
public partial class SampleJsonSerializerContext : JsonSerializerContext
{
	static Type[] _extra =
	[
		typeof(SamplePublicModel),
		typeof(SampleTaskModel),
		typeof(SampleTodoTaskPoco),
		typeof(StorableModel),
		typeof(Item),
		typeof(MyPocoTask),
	];

	public static JsonSerializerOptions DefaultOptions { get; }

	static SampleJsonSerializerContext()
	{
		foreach (var type in _extra)
		{
			Activator.CreateInstance(type);
		}
		DefaultOptions = new JsonSerializerOptions(Default.Options)
		{
			TypeInfoResolver = JsonTypeInfoResolver.Combine(Default, new SynqraJsonTypeInfoResolver(_extra)),
		};
		// remove first dups if any (this is better than avoid registration and allow someone to consume it without ObjectConverter at all)
		for (int i = DefaultOptions.Converters.Count - 1; i >= 0; i--)
		{
			if (DefaultOptions.Converters[i] is ObjectConverter)
			{
				DefaultOptions.Converters.RemoveAt(i);
			}
		}
		DefaultOptions.Converters.Add(new ObjectConverter(_extra));
	}
}
