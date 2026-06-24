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
		var obj = new CommandCreatedEvent
		{
			CommandId = Guid.NewGuid(),
			StreamId = Guid.NewGuid(),
			EventId = Guid.NewGuid(),
			Data = new CreateObjectCommand
			{
				Data = new ObjectData
				{
					["Subject"] = subject,
				},
			},
		};
		async Task Check(JsonSerializerOptions ctx)
		{
			var json = JsonSerializer.Serialize<Event>(obj, ctx.Indented());
			Console.WriteLine(json);

			// Data is now the canonical property bag (ObjectData), serialized as a plain JSON
			// object — no "_t" discriminator and no SampleTaskModel reference. That loose,
			// polymorphic-POCO payload is exactly the contract this redesign removed.
			await Assert.That(json.Contains("SampleTaskModel")).IsFalse();
			await Assert.That(json.Contains(subject)).IsTrue();

			var deserializedObj = JsonSerializer.Deserialize<Event>(json, ctx);
			await Assert.That(deserializedObj).IsNotNull();
			await Assert.That(deserializedObj.CommandId).IsEqualTo(obj.CommandId);
			var createdEvent = (CommandCreatedEvent)deserializedObj;
			await Assert.That(createdEvent.Data).IsNotNull();
			var createCommand = (CreateObjectCommand)createdEvent.Data;
			await Assert.That(createCommand.Data).IsNotNull();
			await Assert.That(GetSubject(createCommand.Data)).IsEqualTo(subject);
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
			StreamId = Guid.NewGuid(),
			EventId = Guid.NewGuid(),
			Data = new CreateObjectCommand
			{
				Data = new ObjectData
				{
					["Subject"] = "Test1",
					["Number"] = 1,
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

			// The payload survives a full serialize → deserialize → serialize cycle as a plain
			// property bag — no "_t"/SampleTaskModel discriminator leaks back in.
			await Assert.That(json2.Contains("SampleTaskModel")).IsFalse();
			await Assert.That(json2.Contains("Test1")).IsTrue();

			var roundTripped = (NewEvent1)deserializedObj;
			var createCommand = (CreateObjectCommand)((CommandCreatedEvent)roundTripped.Event).Data;
			await Assert.That(GetSubject(createCommand.Data)).IsEqualTo("Test1");
		}
		await Check(SampleJsonSerializerContext.DefaultOptions);
		// await Check(AppJsonContext.Default);
	}

	// Dictionary-key casing differs per context (camelCase vs as-is), so look the value up
	// case-insensitively rather than assuming a single key spelling.
	static string? GetSubject(ObjectData data)
	{
		foreach (var (key, value) in data)
		{
			if (string.Equals(key, "Subject", StringComparison.OrdinalIgnoreCase))
			{
				return value as string;
			}
		}
		return null;
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

