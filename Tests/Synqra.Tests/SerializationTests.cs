using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.SampleModels;
using Synqra.Tests.SampleModels.Serialization;
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
#if NET8_0_OR_GREATER
		await Assert.That(JsonSerializer.IsReflectionEnabledByDefault).IsFalse();
#endif

		var subject = "Test Subject " + Guid.NewGuid().ToString("N");
		var obj = new SampleTodoTask
		{
			Subject = subject,
		};
		var jsonOptions = new JsonSerializerOptions(SampleJsonSerializerContext.Default.Options)
		{
			IndentCharacter = '\t',
			IndentSize = 1,
			WriteIndented = true,
		};
		var json = JsonSerializer.Serialize(obj, jsonOptions);
		await Assert.That(json.NormalizeNewLines()).IsEqualTo($$"""
{
	"subject": "{{subject}}"
}
""".NormalizeNewLines());
		var deserializedObj = JsonSerializer.Deserialize(json, SampleJsonSerializerContext.Default.SampleTodoTask);
		await Assert.That(deserializedObj).IsNotNull();
		await Assert.That(deserializedObj.Subject).IsEqualTo(subject);
	}

	[Test]
	public async Task TestSerializationEvent()
	{
		var subject = "Test Subject " + Guid.NewGuid().ToString("N");
		var obj = new CommandCreatedEvent
		{
			CommandId = Guid.NewGuid(),
			ContainerId = Guid.NewGuid(),
			EventId = Guid.NewGuid(),
			Data = new CreateObjectCommand
			{
				Data = new Dictionary<string, object>
				{
					["subject"] = "Test1",
				},
			},
		};
		async Task Check(JsonSerializerContext ctx)
		{
			var jsonOptions = new JsonSerializerOptions(ctx.Options)
			{
				IndentCharacter = '\t',
				IndentSize = 1,
				WriteIndented = true,
			};
			var json = JsonSerializer.Serialize<Event>(obj, jsonOptions);
			await Assert.That(json.NormalizeNewLines()).IsEqualTo($$"""
	{
		"_t": "CommandCreatedEvent",
		"data": {
			"_t": "CreateObjectCommand",
			"data": {
				"subject": "Test1"
			},
			"targetTypeId": "00000000-0000-0000-0000-000000000000",
			"collectionId": "00000000-0000-0000-0000-000000000000",
			"targetId": "00000000-0000-0000-0000-000000000000",
			"commandId": "{{obj.Data.CommandId}}",
			"containerId": "00000000-0000-0000-0000-000000000000"
		},
		"eventId": "{{obj.EventId}}",
		"commandId": "{{obj.CommandId}}"
	}
	""".NormalizeNewLines());
			var deserializedObj = JsonSerializer.Deserialize<Event>(json, jsonOptions);
			await Assert.That(deserializedObj).IsNotNull();
			await Assert.That(deserializedObj.CommandId).IsEqualTo(obj.CommandId);
		}
		await Check(SampleJsonSerializerContext.Default);
		await Check(AppJsonContext.Default);
	}

	[Test]
	public async Task TestSerializationNetworkOperation()
	{
		var @event = new CommandCreatedEvent
		{
			CommandId = Guid.NewGuid(),
			ContainerId = Guid.NewGuid(),
			EventId = Guid.NewGuid(),
			Data = new CreateObjectCommand
			{
				Data = new Dictionary<string, object>
				{
					["subject"] = "Test1",
				},
			},
		};
		var operation = new NewEvent1
		{
			Event = @event,
		};
		async Task Check(JsonSerializerContext ctx)
		{
			var jsonOptions = new JsonSerializerOptions(ctx.Options)
			{
				IndentCharacter = '\t',
				IndentSize = 1,
				WriteIndented = true,
			};
			var json = JsonSerializer.Serialize<TransportOperation>(operation, jsonOptions);
			var deserializedObj = JsonSerializer.Deserialize<TransportOperation>(json, jsonOptions);
			var json2 = JsonSerializer.Serialize<TransportOperation>(deserializedObj, jsonOptions);
			await Assert.That(json2.NormalizeNewLines()).IsEqualTo($$"""
	{
		"_t": "NewEvent1",
		"event": {
			"_t": "CommandCreatedEvent",
			"data": {
				"_t": "CreateObjectCommand",
				"data": {
					"subject": "Test1"
				},
				"targetTypeId": "00000000-0000-0000-0000-000000000000",
				"collectionId": "00000000-0000-0000-0000-000000000000",
				"targetId": "00000000-0000-0000-0000-000000000000",
				"commandId": "{{@event.Data.CommandId}}",
				"containerId": "00000000-0000-0000-0000-000000000000"
			},
			"eventId": "{{@event.EventId}}",
			"commandId": "{{@event.CommandId}}"
		}
	}
	""".NormalizeNewLines());
		}
		await Check(SampleJsonSerializerContext.Default);
		await Check(AppJsonContext.Default);
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

#if NET8_0_OR_GREATER
	[Test]
	public async Task Should_aot2()
	{
		await Assert.That(RuntimeFeature.IsDynamicCodeSupported).IsFalse();
	}
#endif
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

