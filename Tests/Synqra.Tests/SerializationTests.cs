using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.TestHelpers;
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
		var json = JsonSerializer.Serialize(obj, DemoTodoJsonSerializerContext.Default.TodoTask);
		var deserializedObj = JsonSerializer.Deserialize(json, DemoTodoJsonSerializerContext.Default.TodoTask);
		await Assert.That(deserializedObj).IsNotNull();
		await Assert.That(deserializedObj.Subject).IsEqualTo(subject);
	}
}

public class StateManagementTests : BaseTest
{
	[Test]
	public async Task Should_reflect_store_state_by_events()
	{
		// ServiceCollection.AddSingleton<IEventStore, FakeStorage>();


		Assert.Fail("Inconclusive test, needs to be implemented");
	}
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