using Microsoft.Extensions.DependencyInjection;
using Synqra.Storage;

using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra.Tests;

using IStorage = IStorage<Event, Guid>;

public class StateManagementTests : BaseTest<ISynqraStoreContext>
{
	JsonSerializerOptions _jsonSerializerOptions => ServiceProvider.GetRequiredService<JsonSerializerOptions>();
	// ISynqraStoreContext _sut => ServiceProvider.GetRequiredService<ISynqraStoreContext>();
	FakeStorage _fakeStorage => ServiceProvider.GetService<FakeStorage>() ?? (FakeStorage)ServiceProvider.GetService<IStorage<Event, Guid>>() ?? (FakeStorage)ServiceProvider.GetService<IStorage>();
	ISynqraCollection<MyPocoTask> _tasks => _sut.GetCollection<MyPocoTask>();

	public StateManagementTests()
	{
		HostBuilder.AddSynqraStoreContext();
		HostBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default); // im not sure yet, context or options
		HostBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions); // im not sure yet, context or options

		HostBuilder.Services.AddSingleton<FakeStorage>();
		// HostBuilder.Services.AddSingleton(typeof(IStorage<,>), typeof(FakeStorage<,>));
		// HostBuilder.Services.AddSingleton<IStorage<Event, Guid>, FakeStorage<Event, Guid>>();
		HostBuilder.Services.AddSingleton<IStorage<Event, Guid>>(sp => sp.GetRequiredService<FakeStorage>());
		HostBuilder.Services.AddSingleton<IStorage>(sp => sp.GetRequiredService<FakeStorage>());

		// HostBuilder.AddJsonLinesStorage();

		// var _fileName = string.Empty;
		// Configuration["JsonLinesStorage:FileName"] = _fileName = $"TestData/data_{Guid.NewGuid():N}_[TypeName].jsonl";
		// Directory.CreateDirectory(Path.GetDirectoryName(_fileName));
	}

	[Test]
	public async Task Should_emit_command_by_setting_property()
	{
		// HostBuilder.AddJsonLinesStorage();
		// HostBuilder.AddSynqraStoreContext();

		var model = new DemoModel();
		_sut.GetCollection<DemoModel>().Add(model);
		await Assert.That(model.Name).IsNotEqualTo("TestName");

		model.Name = "TestName"; // this should emit a command and broadcast it

		var commands = _sut.GetCollection<ISynqraCommand>().ToArray();

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

		// But it also needs to be applied
		await Assert.That(model.Name).IsEqualTo("TestName");
	}

	[Test]
	public async Task Should_10_create_object()
	{
		var t = new MyPocoTask { Subject = "Test Task" };
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
		await Assert.That(events).HasCount(3);
		var commandCreated = events[0];
		await Assert.That(commandCreated).IsTypeOf<CommandCreatedEvent>();
		var objectCreated = events[1];
		await Assert.That(objectCreated).IsTypeOf<ObjectCreatedEvent>();
		var propertyChanged = events[2];
		await Assert.That(propertyChanged).IsTypeOf<ObjectPropertyChangedEvent>();

		var tasks = _sut.GetCollection<MyPocoTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks[0].Subject).IsEqualTo("Test Task");

		// reopen
		var bt = new StateManagementTests();
		bt.ServiceCollection.AddSingleton(_fakeStorage);
		bt.ServiceCollection.AddSingleton<IStorage>(_fakeStorage);
		var reopened = bt.ServiceProvider.GetRequiredService<ISynqraStoreContext>();

		tasks = _sut.GetCollection<MyPocoTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks[0].Subject).IsEqualTo("Test Task");
	}

	[Test]
	public async Task Should_20_change_object()
	{
		var t = new MyPocoTask { Subject = "Test Task" };
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
		await Assert.That(events).HasCount(5);
		await Assert.That(events[4]).IsTypeOf<ObjectPropertyChangedEvent>();

		await Assert.That(_tasks).HasCount(1);
		await Assert.That(_tasks[0].Subject).IsEqualTo("123");

		// reopen
		var bt = new StateManagementTests();
		bt.ServiceCollection.AddSingleton(_fakeStorage);
		bt.ServiceCollection.AddSingleton<IStorage>(_fakeStorage);
		var reopened = bt.ServiceProvider.GetRequiredService<ISynqraStoreContext>();

		var tasks = _sut.GetCollection<MyPocoTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks[0].Subject).IsEqualTo("123");
	}

	[Test]
	public async Task Should_instantiate_collection_by_ctor()
	{
		var tasks = _sut.GetCollection<MyPocoTask>();
	}

	[Test]
	public async Task Should_instantiate_collection_by_type()
	{
		var tasks = _sut.GetCollection(typeof(MyPocoTask));
	}
}

/// <summary>
/// THIS IS POCO, NOT GENERATED, do not make it partial
/// </summary>
public class MyPocoTask
{
	/// <summary>
	/// THIS IS POCO, NOT GENERATED, do not make it partial
	/// </summary>
	public MyPocoTask()
	{
		
	}
	public string Subject { get; set; }
}

public class FakeStorage : FakeStorage<Event, Guid>, IStorage
{
}

public class FakeStorage<T, TKey> : IStorage<T, TKey>
	// where T : IIdentifiable<TKey>
{
	public List<T> Items { get; } = new List<T>();

	public Task AppendAsync(T item)
	{
		Items.Add(item);
		return Task.CompletedTask;
	}

	public void Dispose()
	{
	}

	public ValueTask DisposeAsync()
	{
		return default;
	}

	public Task FlushAsync()
	{
		return Task.CompletedTask;
	}

	public async IAsyncEnumerable<T> GetAll(TKey? from = default, CancellationToken? cancellationToken = null)
	{
		foreach (T item in Items)
		{
			// if (from == null || item is IEvent e && e.Id > from)
			{
				yield return item;
			}
		}
	}

}

public partial class DemoModel
{
	public partial string Name { get; set; }
	public partial string Prprpr { get; set; }
}
