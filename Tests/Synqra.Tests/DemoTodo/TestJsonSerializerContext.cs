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
