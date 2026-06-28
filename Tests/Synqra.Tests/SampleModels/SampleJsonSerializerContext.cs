using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using SharpCompress.Common;
using Synqra.Projection.File;
using Synqra.Tests;
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
[JsonSerializable(typeof(StorableModel))]
[JsonSerializable(typeof(SampleOnePropertyObject))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(ObjectData))]
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
[JsonSerializable(typeof((Guid, Guid)))]
[JsonSerializable(typeof(Int64))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(object))]

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

[JsonSerializable(typeof(SampleFieldFloatModel))]
[JsonSerializable(typeof(SampleFieldDoubleModel))]
[JsonSerializable(typeof(SampleFieldNullableFloatModel))]
[JsonSerializable(typeof(SampleFieldNullableDoubleModel))]

[JsonSerializable(typeof(SampleFieldListIntModel))]
[JsonSerializable(typeof(SampleFieldListObjectModel))]
[JsonSerializable(typeof(SampleFieldListBaseModel))]
[JsonSerializable(typeof(SampleFieldListSealedModel))]
[JsonSerializable(typeof(SampleFieldListBaseModel_))]
[JsonSerializable(typeof(SampleFieldEnumerableBaseModel))]
[JsonSerializable(typeof(SampleFieldEnumerableBaseModel_))]

[JsonSerializable(typeof(SampleFieldDictionaryStringObjectModel))]

[JsonSerializable(typeof(List<SampleBaseModel>))]
[JsonSerializable(typeof(List<SampleDerivedModel>))]
[JsonSerializable(typeof(List<SampleSealedModel>))]
[JsonSerializable(typeof(List<SampleFieldDictionaryStringObjectModel>))]

[JsonSerializable(typeof(TestGraphNode))]
[JsonSerializable(typeof(HierarchyLink))]
[JsonSerializable(typeof(DependsOn))]
[JsonSerializable(typeof(RelatedTo))]
[JsonSerializable(typeof(WeightedLink))]
[JsonSerializable(typeof(TestDocNode))]
[JsonSerializable(typeof(TestFolderNode))]
[JsonSerializable(typeof(TestTagNode))]
[JsonSerializable(typeof(FiledIn))]
[JsonSerializable(typeof(TaggedWith))]

[JsonSerializable(typeof(TestGeneratedContainerNode))]
[JsonSerializable(typeof(TestUniqueComponent))]
[JsonSerializable(typeof(TestTaggingComponent))]
[JsonSerializable(typeof(TestActivatableComponent))]

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
		typeof(DemoModel),
		typeof(TestGraphNode),
		typeof(HierarchyLink),
		typeof(DependsOn),
		typeof(RelatedTo),
		typeof(WeightedLink),
		typeof(TestDocNode),
		typeof(TestFolderNode),
		typeof(TestTagNode),
		typeof(FiledIn),
		typeof(TaggedWith),
		typeof(TestGeneratedContainerNode),
		typeof(TestUniqueComponent),
		typeof(TestTaggingComponent),
		typeof(TestActivatableComponent),
	];

	static readonly object __sync = new object();


	public static JsonSerializerOptions DefaultOptions
	{
		get
		{
			// Default
			if (field == null)
			{
				lock (__sync)
				{
					if (field == null)
					{
						foreach (var type in _extra)
						{
							SynqraJsonTypeInfoResolver.RegisterGeneratedModel(type);
							// Activator.CreateInstance(type);
						}
						var options = new JsonSerializerOptions(Default.Options)
						{
							TypeInfoResolver = new SynqraJsonTypeInfoResolver(_extra),
							// TypeInfoResolver = JsonTypeInfoResolver.Combine(new SynqraJsonTypeInfoResolver(_extra), Default),
						};
						// remove first dups if any (this is better than avoid registration and allow someone to consume it without ObjectConverter at all)
						for (int i = options.Converters.Count - 1; i >= 0; i--)
						{
							if (options.Converters[i] is ObjectConverter)
							{
								options.Converters.RemoveAt(i);
							}
						}
						options.Converters.Add(new ObjectConverter(_extra));
						field = options;
					}
				}
			}
			return field;
		}
	}
}
