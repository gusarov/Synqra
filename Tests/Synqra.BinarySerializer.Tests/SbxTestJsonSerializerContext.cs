using Synqra.Tests.BinarySerialization;
using Synqra.Tests.SampleModels.Binding;
using Synqra.Tests.SampleModels.Serialization;
using Synqra.Tests.SampleModels.Syncronization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra.BinarySerializer.Tests;

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
	, Converters = [typeof(ObjectConverter)]
)]
[JsonSerializable(typeof(SampleTestDataPoco))]
[JsonSerializable(typeof(SampleTaskModel))]

[JsonSerializable(typeof(SampleFieldIntModel))]
[JsonSerializable(typeof(SampleFieldFloatModel))]
[JsonSerializable(typeof(SampleFieldDoubleModel))]
[JsonSerializable(typeof(SampleFieldNullableFloatModel))]
[JsonSerializable(typeof(SampleFieldNullableDoubleModel))]
[JsonSerializable(typeof(SampleFieldObjectModel))]
[JsonSerializable(typeof(SampleFieldDictionaryStringObjectModel))]
[JsonSerializable(typeof(SampleFieldBaseModel))]
[JsonSerializable(typeof(SampleFieldDerrivedModel))]
[JsonSerializable(typeof(SampleFieldSealedDerivedModel))]
[JsonSerializable(typeof(SampleFieldSealedModel))]
[JsonSerializable(typeof(SampleFieldListIntModel))]
[JsonSerializable(typeof(SampleFieldListObjectModel))]
[JsonSerializable(typeof(SampleFieldListBaseModel))]
[JsonSerializable(typeof(SampleFieldEnumerableBaseModel))]
[JsonSerializable(typeof(SampleFieldListSealedModel))]
[JsonSerializable(typeof(SampleBaseModel))]
[JsonSerializable(typeof(SampleDerivedModel))]
[JsonSerializable(typeof(SampleSealedDerivedModel))]
[JsonSerializable(typeof(SampleSealedModel))]

[JsonSerializable(typeof(Synqra.TransportOperation))]
[JsonSerializable(typeof(Synqra.NewEvent1))]
[JsonSerializable(typeof(Synqra.CommandCreatedEvent))]
[JsonSerializable(typeof(Synqra.ObjectCreatedEvent))]
[JsonSerializable(typeof(Synqra.ObjectPropertyChangedEvent))]
[JsonSerializable(typeof(Synqra.CreateObjectCommand))]
[JsonSerializable(typeof(Synqra.ObjectData))]
[JsonSerializable(typeof(Synqra.Command))]
[JsonSerializable(typeof(Synqra.Event))]

[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(List<SampleBaseModel>))]
[JsonSerializable(typeof(List<SampleDerivedModel>))]
[JsonSerializable(typeof(List<SampleSealedModel>))]

[JsonConverter(typeof(ObjectConverter))]
public partial class SbxTestJsonSerializerContext : JsonSerializerContext
{
	static readonly Type[] _extra =
	[
		typeof(SampleTaskModel),
		typeof(SampleTestDataPoco),
	];

	static readonly object _sync = new object();

	public static JsonSerializerOptions DefaultOptions
	{
		get
		{
			if (field == null)
			{
				lock (_sync)
				{
					if (field == null)
					{
						foreach (var type in _extra)
						{
							SynqraJsonTypeInfoResolver.RegisterGeneratedModel(type);
						}
						var options = new JsonSerializerOptions(Default.Options)
						{
							TypeInfoResolver = new SynqraJsonTypeInfoResolver(_extra),
						};
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
