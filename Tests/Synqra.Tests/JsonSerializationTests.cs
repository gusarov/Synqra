using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.SampleModels;
using Synqra.Tests.SampleModels.Serialization;
using Synqra.Tests.SampleModels.Syncronization;
using Synqra.Tests.TestHelpers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra.Tests;

/// <summary>
/// This tests is about STJ serialization and deserialization for network or storage purposes, for Poco or generated mindable models, for nested objects, polymorphic objects, etc.
/// </summary>
public class JsonSerializationTests
{
	[Test]
	public async Task Should_05_serialize()
	{
#if NET8_0_OR_GREATER
		await Assert.That(JsonSerializer.IsReflectionEnabledByDefault).IsFalse();
#endif

		var subject = "Test Subject " + Guid.NewGuid().ToString("N");
		var obj = new SampleTodoTaskPoco
		{
			Subject = subject,
		};
		var json = JsonSerializer.Serialize(obj, SampleJsonSerializerContext.DefaultOptions.Indented());
		using var __ = Assert.Multiple();
		await Assert.That(json.NormalizeNewLines()).IsEqualTo($$"""
		{
			"Subject": "{{subject}}"
		}
		""".NormalizeNewLines());
		var deserializedObj = JsonSerializer.Deserialize(json, SampleJsonSerializerContext.Default.SampleTodoTaskPoco);
		await Assert.That(deserializedObj).IsNotNull();
		await Assert.That(deserializedObj.Subject).IsEqualTo(subject);
	}

	[Test]
	public async Task Should_10_serialize_object()
	{
#if NET8_0_OR_GREATER
		await Assert.That(JsonSerializer.IsReflectionEnabledByDefault).IsFalse();
#endif

		var subject = "Test Subject " + Guid.NewGuid().ToString("N");
		var obj = new SampleTodoTaskPoco
		{
			Subject = subject,
		};
		var json = JsonSerializer.Serialize<object>(obj, SampleJsonSerializerContext.DefaultOptions.Indented());
		using var __ = Assert.Multiple();
		await Assert.That(json.NormalizeNewLines()).IsEqualTo($$"""
{
	"_t": "SampleTodoTaskPoco",
	"Subject": "{{subject}}"
}
""".NormalizeNewLines());
		var deserializedObj = (SampleTodoTaskPoco)JsonSerializer.Deserialize<object>(json, SampleJsonSerializerContext.DefaultOptions);
		await Assert.That(deserializedObj).IsNotNull();
		await Assert.That(deserializedObj.Subject).IsEqualTo(subject);
	}

	[Test]
	[Arguments(1)]
	[Arguments(2)]
	// [Arguments(3)]
	// [Arguments(4)]
	public async Task Should_20_serialize_event(int ctxId)
	{
		var subject = "Test Subject " + Guid.NewGuid().ToString("N");
		var obj = new CommandCreatedEvent
		{
			CommandId = Guid.NewGuid(),
			ContainerId = Guid.NewGuid(),
			EventId = Guid.NewGuid(),
			Data = new CreateObjectCommand
			{
				/*
				Data = new Dictionary<string, object>
				{
					["subject"] = "Test1",
				},
				*/
				Data = new SampleTaskModel
				{
					Subject = subject,
				},
			},
		};
		async Task Check(JsonSerializerOptions ctx)
		{
			var json = JsonSerializer.Serialize<Event>(obj, ctx.Indented());
			// using var __ = Assert.Multiple();
			Console.WriteLine(json);
			await Assert.That(json.NormalizeNewLines()).IsEqualTo($$"""
			{
				"_t": "CommandCreatedEvent",
				"Data": {
					"_t": "CreateObjectCommand",
					"Data": {
						"_t": "SampleTaskModel",
						"Subject": "{{subject}}",
						"Number": 0
					},
					"TargetTypeId": "00000000-0000-0000-0000-000000000000",
					"CollectionId": "00000000-0000-0000-0000-000000000000",
					"TargetId": "00000000-0000-0000-0000-000000000000",
					"CommandId": "{{obj.Data.CommandId}}",
					"ContainerId": "00000000-0000-0000-0000-000000000000"
				},
				"EventId": "{{obj.EventId}}",
				"CommandId": "{{obj.CommandId}}"
			}
			""".NormalizeNewLines());
			var deserializedObj = JsonSerializer.Deserialize<Event>(json, ctx);
			await Assert.That(deserializedObj).IsNotNull();
			await Assert.That(deserializedObj.CommandId).IsEqualTo(obj.CommandId);
			var createdEvent = (CommandCreatedEvent)deserializedObj;
			await Assert.That(createdEvent.Data).IsNotNull();
			var createCommand = (CreateObjectCommand)createdEvent.Data;
			await Assert.That(createCommand.Data).IsNotNull();
			var taskModel = (SampleTaskModel)createCommand.Data;
			await Assert.That(taskModel.Subject).IsEqualTo(subject);
		}
		switch (ctxId)
		{
			case 1:
				await Check(SampleJsonSerializerContext.DefaultOptions);
				break;
			case 2:
				await Check(AppJsonContext.DefaultOptions);
				break;
			case 3:
				await Check(SampleJsonSerializerContext.Default.Options);
				break;
			case 4:
				await Check(AppJsonContext.Default.Options);
				break;
			default:
				throw new Exception();
		}
	}

	[Test]
	public async Task Should_30_serialize_network_operation()
	{
		var @event = new CommandCreatedEvent
		{
			CommandId = Guid.NewGuid(),
			ContainerId = Guid.NewGuid(),
			EventId = Guid.NewGuid(),
			Data = new CreateObjectCommand
			{
				/*
				Data = new Dictionary<string, object>
				{
					["subject"] = "Test1",
				},
				*/
				Data = new SampleTaskModel
				{
					Subject = "Test1",
					Number = 1,
				},
			},
		};
		var operation = new NewEvent1
		{
			Event = @event,
		};
		async Task Check(JsonSerializerOptions options)
		{
			var jsonOptions = new JsonSerializerOptions(options)
			{
				IndentCharacter = '\t',
				IndentSize = 1,
				WriteIndented = true,
			};
			var json = JsonSerializer.Serialize<TransportOperation>(operation, jsonOptions);
			var deserializedObj = JsonSerializer.Deserialize<TransportOperation>(json, jsonOptions);
			var json2 = JsonSerializer.Serialize<TransportOperation>(deserializedObj, jsonOptions);
			Console.WriteLine(json2.NormalizeNewLines());
			await Assert.That(json2.NormalizeNewLines()).IsEqualTo($$"""
	{
		"_t": "NewEvent1",
		"Event": {
			"_t": "CommandCreatedEvent",
			"Data": {
				"_t": "CreateObjectCommand",
				"Data": {
					"_t": "SampleTaskModel",
					"Subject": "Test1",
					"Number": 1
				},
				"TargetTypeId": "00000000-0000-0000-0000-000000000000",
				"CollectionId": "00000000-0000-0000-0000-000000000000",
				"TargetId": "00000000-0000-0000-0000-000000000000",
				"CommandId": "{{@event.Data.CommandId}}",
				"ContainerId": "00000000-0000-0000-0000-000000000000"
			},
			"EventId": "{{@event.EventId}}",
			"CommandId": "{{@event.CommandId}}"
		}
	}
	""".NormalizeNewLines());
		}
		await Check(SampleJsonSerializerContext.DefaultOptions);
		// await Check(AppJsonContext.Default);
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

