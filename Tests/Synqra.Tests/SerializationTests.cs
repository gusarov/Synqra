using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.TestHelpers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra.Tests;

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

public class AotTests
{
	/*
	[Test]
	public async Task Should_aot1()
	{
		// ServiceCollection.AddSingleton<IEventStore, FakeStorage>();
		Console.WriteLine();
		GetType().GetMethod(nameof(Should_)).Invoke(this, []);
	}

	[Test]
	public async Task Should_()
	{
		throw new Exception("Success");
		Assert.Fail("Success");
	}
	*/

	[Test]
	public async Task Should_aot2()
	{
		await Assert.That(RuntimeFeature.IsDynamicCodeSupported).IsFalse();
	}

	/*
	[Test]
	public async Task Should_aot3()
	{
		var ex = await Assert.ThrowsAsync(async () => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new System.Reflection.AssemblyName("TestAssembly"), System.Reflection.Emit.AssemblyBuilderAccess.Run));
		await Assert.That(ex).IsTypeOf<PlatformNotSupportedException>();
	}

	[Test]
	public async Task Should_aot4()
	{
		var r = System.Reflection.Assembly.LoadFile(Path.GetFullPath("Synqra.Storage.Jsonl.dll"));
	}
	*/
}

public record Person(string Name, int Age, int Height)
{
	public string Name { get; set; } = Name;
	public int Age { get; init; } = Age;
}

public class BaseTestTests : BaseTest
{
	[Test]
	public async Task Should_allow_configure_di_and_use_it()
	{
		ServiceCollection.AddSingleton<IDemoService, DemoService>();

		var ds = ServiceProvider.GetRequiredService<IDemoService>();

		await Assert.That(ds).IsNotNull();
	}
}

public interface IDemoService
{
}

public class DemoService : IDemoService
{

}