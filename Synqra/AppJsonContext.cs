using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web
	, AllowTrailingCommas = true
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
	, Converters = [
		typeof(ObjectConverter)
	]
)]
[JsonSerializable(typeof(Synqra.Event))]
[JsonSerializable(typeof(Synqra.CommandCreatedEvent))]
[JsonSerializable(typeof(Synqra.Command))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Int64))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(TransportOperation))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonConverter(typeof(ObjectConverter))]
public partial class AppJsonContext : JsonSerializerContext
{
}
