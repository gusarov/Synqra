using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Synqra;

[JsonSourceGenerationOptions(
	  AllowTrailingCommas = true
	, DefaultBufferSize = 16 * 1024
	, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
[JsonSerializable(typeof(Synqra.Event))]
[JsonSerializable(typeof(Synqra.CommandCreatedEvent))]
[JsonSerializable(typeof(Synqra.Command))]
[JsonSerializable(typeof(Synqra.IBindableModel))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(Int16))]
[JsonSerializable(typeof(UInt16))]
[JsonSerializable(typeof(Int32))]
[JsonSerializable(typeof(UInt32))]
[JsonSerializable(typeof(Int64))]
[JsonSerializable(typeof(UInt64))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(TransportOperation))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
public partial class AppJsonContext : JsonSerializerContext
{
	public static JsonSerializerOptions DefaultOptions { get; }

	static AppJsonContext()
	{
		DefaultOptions = new JsonSerializerOptions(Default.Options)
		{
			TypeInfoResolver = JsonTypeInfoResolver.Combine(new SynqraJsonTypeInfoResolver(/*_extra*/), Default),
		};
		/*
		// remove first dups if any (this is better than avoid registration and allow someone to consume it without ObjectConverter at all)
		for (int i = DefaultOptions.Converters.Count - 1; i >= 0; i--)
		{
			if (DefaultOptions.Converters[i] is ObjectConverter)
			{
				DefaultOptions.Converters.RemoveAt(i);
			}
		}
		DefaultOptions.Converters.Add(new ObjectConverter(_extra));
		*/
	}

}
