using Synqra.Tests.ModelManagement;
using Synqra.Tests.Performance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Synqra.Tests.DemoTodo;

public class TodoTask
{
	public string Subject { get; set; }
}

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
#if DEBUG
	WriteIndented = true,
#endif
	GenerationMode = JsonSourceGenerationMode.Default,
	DefaultBufferSize = 16384,
	IgnoreReadOnlyFields = false,
	IgnoreReadOnlyProperties = false,
	IncludeFields = false,
	AllowTrailingCommas = true,
	ReadCommentHandling = JsonCommentHandling.Skip,
	DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase
)]
[JsonSerializable(typeof(DemoObject))]
[JsonSerializable(typeof(DemoModel))]
[JsonSerializable(typeof(ISynqraCommand))]
[JsonSerializable(typeof(TodoTask))]
[JsonSerializable(typeof(TestItem))]
[JsonSerializable(typeof(MyTask))]
[JsonSerializable(typeof(Synqra.Event))]
[JsonSerializable(typeof(Synqra.Command))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(JsonElement))]
// [JsonSerializable(typeof(Command))]
// [JsonSerializable(typeof(Event))]
public partial class TestJsonSerializerContext : JsonSerializerContext
{
	/*    
	public static JsonSerializerOptions Options = new JsonSerializerOptions
	{
	};
	*/

	/*

	public DemoTodoJsonSerializerContext(JsonSerializerOptions? options) : base(options)
	{
	}

	protected override JsonSerializerOptions? GeneratedSerializerOptions => throw new NotImplementedException();

	public override JsonTypeInfo? GetTypeInfo(Type type)
	{
		throw new NotImplementedException();
	}
	*/
}
