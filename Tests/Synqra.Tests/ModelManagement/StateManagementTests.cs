using Microsoft.Extensions.DependencyInjection;
using Synqra.Storage;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.TestHelpers;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra.Tests.ModelManagement;

public class StateManagementTests : BaseTest<ISynqraStoreContext>
{
	JsonSerializerOptions _jsonSerializerOptions => ServiceProvider.GetRequiredService<JsonSerializerOptions>();
	// ISynqraStoreContext _sut => ServiceProvider.GetRequiredService<ISynqraStoreContext>();
	FakeStorage _fakeStorage => ServiceProvider.GetRequiredService<FakeStorage>();
	ISynqraCollection<MyTask> _tasks => _sut.GetCollection<MyTask>();

	public StateManagementTests()
	{
		HostBuilder.AddSynqraStoreContext();
		HostBuilder.Services.AddSingleton<JsonSerializerContext>(TestJsonSerializerContext.Default); // im not sure yet, context or options
		HostBuilder.Services.AddSingleton(TestJsonSerializerContext.Default.Options); // im not sure yet, context or options
		HostBuilder.Services.AddSingleton<FakeStorage>();
		HostBuilder.Services.AddSingleton<IStorage>(sp => sp.GetRequiredService<FakeStorage>());

		// HostBuilder.AddJsonLinesStorage();

		// var _fileName = string.Empty;
		// Configuration["JsonLinesStorage:FileName"] = _fileName = $"TestData/data_{Guid.NewGuid():N}_[TypeName].jsonl";
		// Directory.CreateDirectory(Path.GetDirectoryName(_fileName));
	}

	[Test]
	public async Task Should_emit_command_by_setting_property()
	{
		HostBuilder.Services.AddSingleton<JsonSerializerContext>(DemoTodo.TestJsonSerializerContext.Default);
		HostBuilder.Services.AddSingleton(DemoTodo.TestJsonSerializerContext.Default.Options);
		HostBuilder.AddJsonLinesStorage();
		HostBuilder.AddSynqraStoreContext();

		var model = new DemoModel();
		_sut.GetCollection<DemoModel>().Add(model);
		model.Name = "TestName"; // this should emit a command and broadcast it

		var commands = _sut.GetCollection<Synqra.ISynqraCommand>().ToArray();

		var jso = ServiceProvider.GetRequiredService<JsonSerializerOptions>();
		Console.WriteLine("Commands:");
		foreach (var item in commands)
		{
			Console.WriteLine(JsonSerializer.Serialize(item, jso) + " // " + item.GetType().Name + " // " + item);
		}
		Console.WriteLine("Commands Done");


		await Assert.That(commands.Count()).IsEqualTo(2);
		var co = (CreateObjectCommand)commands[0];

		var cop = (ChangeObjectPropertyCommand)commands[1];
		await Assert.That(cop.PropertyName).IsEqualTo(nameof(model.Name));
		await Assert.That(cop.OldValue).IsEqualTo(null);
		await Assert.That(cop.NewValue).IsEqualTo("TestName");
	}

	[Test]
	public async Task Should_10_create_object()
	{
		var t = new MyTask { Subject = "Test Task" };
		_tasks.Add(t);

		var events = _fakeStorage.Items.OfType<Event>().ToArray();
		foreach (var item in events)
		{
			Console.WriteLine(JsonSerializer.Serialize(item, _jsonSerializerOptions));
		}

		// objects
		await Assert.That(_tasks).HasCount(1);
		await Assert.That(_tasks[0].Subject).IsEqualTo("Test Task");
		await Assert.That(_tasks[0]).IsEquivalentTo(t);
		await Assert.That(ReferenceEquals(_tasks[0], t)).IsTrue();

		// events
		await Assert.That(events).HasCount(2);
		var commandCreated = events[0];
		await Assert.That(commandCreated).IsTypeOf<CommandCreatedEvent>();
		var objectCreated = events[1];
		await Assert.That(objectCreated).IsTypeOf<ObjectCreatedEvent>();

		var tasks = _sut.GetCollection<MyTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks[0].Subject).IsEqualTo("Test Task");

		// reopen
		var bt = new StateManagementTests();
		bt.ServiceCollection.AddSingleton(_fakeStorage);
		bt.ServiceCollection.AddSingleton<IStorage>(_fakeStorage);
		var reopened = bt.ServiceProvider.GetRequiredService<ISynqraStoreContext>();

		tasks = _sut.GetCollection<MyTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks[0].Subject).IsEqualTo("Test Task");
	}

	[Test]
	public async Task Should_20_change_object()
	{
		var t = new MyTask { Subject = "Test Task" };
		_tasks.Add(t);

		using (_tasks.PocoTracker(t))
		{
			t.Subject = "123"; // There should be event driven mode that helps to track the changes, but POCO also must work, so need snapshotting
		}

		var events = _fakeStorage.Items.OfType<Event>().ToArray();
		foreach (var item in events)
		{
			Console.WriteLine($"{item.GetType().Name} {item}");
		}
		await Assert.That(events).HasCount(4);
		await Assert.That(events[3]).IsTypeOf<ObjectPropertyChangedEvent>();

		await Assert.That(_tasks).HasCount(1);
		await Assert.That(_tasks[0].Subject).IsEqualTo("123");

		// reopen
		var bt = new StateManagementTests();
		bt.ServiceCollection.AddSingleton(_fakeStorage);
		bt.ServiceCollection.AddSingleton<IStorage>(_fakeStorage);
		var reopened = bt.ServiceProvider.GetRequiredService<ISynqraStoreContext>();

		var tasks = _sut.GetCollection<MyTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks[0].Subject).IsEqualTo("123");
	}

	[Test]
	public async Task Should_instantiate_collection_by_ctor()
	{
		var tasks = _sut.GetCollection<MyTask>();
	}

	[Test]
	public async Task Should_instantiate_collection_by_type()
	{
		var tasks = _sut.GetCollection(typeof(MyTask));
	}
}

/// <summary>
/// THIS IS POCO, NOT GENERATED, do not make it partial
/// </summary>
public class MyTask
{
	public string Subject { get; set; }
}

public class FakeStorage : IStorage
{
	public List<object> Items { get; } = new List<object>();
	bool _appending = false;

	public Task AppendAsync<T>(T item)
	{
		_appending = true;
		Items.Add(item);
		return Task.CompletedTask;
	}

	public void Dispose()
	{
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}

	public async IAsyncEnumerable<T> GetAll<T>()
	{
		if (_appending)
		{
			throw new Exception("Cannot read storage after it started writing into it");
		}
		foreach (T item in Items)
		{
			yield return item;
		}
	}
}

public partial class DemoModel
{
	public partial string Name { get; set; }
	public partial string Prprpr { get; set; }
}
