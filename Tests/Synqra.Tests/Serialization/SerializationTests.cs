using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.TestHelpers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra.Tests.Serialization;

public class SerializationTests
{
	[Test]
	public async Task TestSerialization()
	{
		await Assert.That(JsonSerializer.IsReflectionEnabledByDefault).IsFalse();

		var subject = "Test Subject " + Guid.NewGuid().ToString("N");
		var obj = new TodoTask
		{
			Subject = subject,
		};
		var jsonOptions = new JsonSerializerOptions(TestJsonSerializerContext.Default.Options)
		{
			IndentCharacter = '\t',
			IndentSize = 1,
			WriteIndented = true,
		};
		var json = JsonSerializer.Serialize(obj, jsonOptions);
		await Assert.That(json).IsEqualTo($$"""
{
	"subject": "{{subject}}"
}
""");
		var deserializedObj = JsonSerializer.Deserialize(json, TestJsonSerializerContext.Default.TodoTask);
		await Assert.That(deserializedObj).IsNotNull();
		await Assert.That(deserializedObj.Subject).IsEqualTo(subject);
	}
}

public record Person(string Name, int Age, int Height)
{
	public string Name { get; set; } = Name;
	public int Age { get; init; } = Age;
}
