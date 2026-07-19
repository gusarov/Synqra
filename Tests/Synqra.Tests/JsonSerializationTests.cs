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
		var deserializedObj = JsonSerializer.Deserialize<SampleTodoTaskPoco>(json, SampleJsonSerializerContext.DefaultOptions);
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
		// Internal-test well-known guids (C0DE prefix, 0000 hash = internal; see docs/model.md §8). Group-3
		// 8000 = version 8 (fixed) + project 0. Group-4 = variant nibble (RFC 10xx free bits: 8=prod, 9=test —
		// so 9 here) + 12-bit class: 900C=command, 9005=container/stream, 9001=class 001 Component (a component
		// *instance* — TargetId/ComponentId, 9001-…003). An all-zero node is a class-self-reference (the type
		// itself); a concrete/user type lives in the F class-space (the low 00x codes are reserved for the
		// built-in kinds), so TargetTypeId/ComponentTypeId here are 9F01-…000 (the SampleTaskModel type).
		// Readable stand-ins — real type ids are v8 hashes, real instances are v7, neither a C0DE value.
		// Commands are spaced by 0x100 so their derived events (Derive(CommandId, ordinal) = CommandId +
		// ordinal) fit in the low byte without colliding with the next command; the CommandCreatedEvent
		// wrapper is ordinal 0, so its EventId == the command id (same 800C space, not a separate event class).
		var cmd = new AddComponentCommand
		{
			CommandId       = new Guid("C0DE0000-0000-8000-900C-000000000100"),
			StreamId        = new Guid("C0DE0000-0000-8000-9005-000000000001"),
			TargetTypeId    = new Guid("C0DE0000-0000-8000-9F01-000000000000"),
			CollectionId    = new Guid("C0DE0000-0000-8000-9002-000000000002"),
			TargetId        = new Guid("C0DE0000-0000-8000-9001-000000000003"),
			ComponentTypeId = new Guid("C0DE0000-0000-8000-9F01-000000000000"),
			ComponentId     = new Guid("C0DE0000-0000-8000-9001-000000000003"),
			Data            = new SampleTaskModel
			{
				Subject = subject,
			},
		};
		var obj = new CommandCreatedEvent
		{
			CommandId = new Guid("C0DE0000-0000-8000-900C-000000000100"),
			StreamId  = new Guid("C0DE0000-0000-8000-9005-000000000001"),
			EventId   = new Guid("C0DE0000-0000-8000-900C-000000000100"),
			Data      = cmd,
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
					"_t": "AddComponentCommand",
					"ComponentTypeId": "{{cmd.ComponentTypeId}}",
					"ComponentId": "{{cmd.ComponentId}}",
					"Data": {
						"_t": "SampleTaskModel",
						"Subject": "{{subject}}",
						"Number": 0
					},
					"TargetTypeId": "{{cmd.TargetTypeId}}",
					"CollectionId": "{{cmd.CollectionId}}",
					"TargetId": "{{cmd.TargetId}}",
					"CommandId": "{{cmd.CommandId}}",
					"StreamId": "{{cmd.StreamId}}"
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
			var createCommand = (AddComponentCommand)createdEvent.Data;
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
		// Internal-test well-known guids (see docs/model.md §8). Group-3 8000 = v8 + project 0; group-4
		// variant nibble 9 = test (RFC 10xx). Class 9001=Component instance (TargetId/ComponentId, 9001-…003);
		// a concrete type is a class-self-reference in the F class-space, so TargetTypeId/ComponentTypeId are
		// 9F01-…000 (the SampleTaskModel type) — readable stand-ins (real type ids are v8 hashes, instances are v7).
		var cmd = new AddComponentCommand
		{
			CommandId       = new Guid("C0DE0000-0000-8000-900C-000000000100"),
			StreamId        = new Guid("C0DE0000-0000-8000-9005-000000000001"),
			TargetTypeId    = new Guid("C0DE0000-0000-8000-9F01-000000000000"),
			CollectionId    = new Guid("C0DE0000-0000-8000-9002-000000000002"),
			TargetId        = new Guid("C0DE0000-0000-8000-9001-000000000003"),
			ComponentTypeId = new Guid("C0DE0000-0000-8000-9F01-000000000000"),
			ComponentId     = new Guid("C0DE0000-0000-8000-9001-000000000003"),
			Data            = new SampleTaskModel
			{
				Subject = "Test1",
				Number  = 1,
			},
		};
		var @event = new CommandCreatedEvent
		{
			CommandId = new Guid("C0DE0000-0000-8000-900C-000000000100"),
			StreamId  = new Guid("C0DE0000-0000-8000-9005-000000000001"),
			EventId   = new Guid("C0DE0000-0000-8000-900C-000000000100"),
			Data      = cmd,
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
				"_t": "AddComponentCommand",
				"ComponentTypeId": "{{cmd.ComponentTypeId}}",
				"ComponentId": "{{cmd.ComponentId}}",
				"Data": {
					"_t": "SampleTaskModel",
					"Subject": "Test1",
					"Number": 1
				},
				"TargetTypeId": "{{cmd.TargetTypeId}}",
				"CollectionId": "{{cmd.CollectionId}}",
				"TargetId": "{{cmd.TargetId}}",
				"CommandId": "{{cmd.CommandId}}",
				"StreamId": "{{cmd.StreamId}}"
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
	/*
	[Test]
	public async Task Should_aot5()
	{
		await Assert.That(SynqraNativeAOT.IsNativeAOT).IsTrue();
	}
	*/
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

	[Test]
	public async Task Should_no_be()
	{
		if (!BitConverter.IsLittleEndian)
		{
			Assert.Fail("This test is only for little-endian platforms");
		}
	}
}

public record Person(string Name, int Age, int Height)
{
	public string Name { get; set; } = Name;
	public int Age { get; init; } = Age;
}

