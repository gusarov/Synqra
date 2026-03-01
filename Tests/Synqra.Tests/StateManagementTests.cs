using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.Projection.InMemory;
#if Sqlite && NET10_0_OR_GREATER
using Microsoft.EntityFrameworkCore;
using Synqra.Projection.Sqlite;
#endif
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Synqra.BinarySerializer;
using Synqra.Projection.File;
using Synqra.AppendStorage.File;
using Synqra.Projection;

namespace Synqra.Tests;

using IAppendStorage = IAppendStorage<Event, Guid>;

[InheritsTests]
public class InMemoryStateManageementTests : StateManagementTests
{
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.AddInMemorySynqraStore();
	}
}

[InheritsTests]
public class FileStateManageementTests : StateManagementTests
{
	string _folder;

	[Before(Test)]
	public void Setup()
	{
		_folder = CreateTestFolder();
	}

	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.AddFileSynqraStore();
		hostApplicationBuilder.AddAppendStorageFile<Event>(e => e.EventId);
		hostApplicationBuilder.AddAppendStorageFile<Command>(e => e.CommandId);
		hostApplicationBuilder.AddAppendStorageFile<Item>(e =>
		{
			if (e.CollectionId == default)
			{
				throw new Exception("Unknown collection id");
			}
			return (e.CollectionId, e.ObjectId);
		});

		hostApplicationBuilder.Configuration["Storage:FileStorage:Folder"] = Path.Combine(_folder, "[Type]") + Path.DirectorySeparatorChar;
	}
}

#if Sqlite && NET10_0_OR_GREATER
[InheritsTests]
public class SqliteStateManageementTests : StateManagementTests
{
	protected override void Registration(IHostApplicationBuilder hostApplicationBuilder)
	{
		hostApplicationBuilder.Configuration["ConnectionStrings:SynqraProjectionSqlite"] = ":memory:"; // DataStore:sqlite_test.db
		hostApplicationBuilder.AddSqliteSynqraStore();
		hostApplicationBuilder.Services.AddSingleton<SqliteDatabaseContext, TestExtendedSqliteDatabaseContext>();
	}
}

public class TestExtendedSqliteDatabaseContext : SqliteDatabaseContext
{
	ILogger _logger;

	public TestExtendedSqliteDatabaseContext()
	{
		
	}

	
	public TestExtendedSqliteDatabaseContext(
		  DbContextOptions<TestExtendedSqliteDatabaseContext> options
		, IConfiguration configuration
		, ILogger<TestExtendedSqliteDatabaseContext> logger
		) : base(
		  true
		, options
		, configuration
		, logger
		)
	{
		_logger = logger;
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		modelBuilder.Entity<MyPocoTask>(b =>
		{
			b.HasKey(t => t.Id);
			b.Property("Subject");
		});

		modelBuilder.Entity<DemoModel>(b =>
		{
			b.HasKey(t => t.Id);
			b.Property("Name");
			b.Property("Prprpr");
		});
	}
}

#endif

public abstract class StateManagementTests : BaseTest<IObjectStore>
{
	JsonSerializerOptions _jsonSerializerOptions => ServiceProvider.GetRequiredService<JsonSerializerOptions>();
	// ISynqraStoreContext _sut => ServiceProvider.GetRequiredService<ISynqraStoreContext>();
	FakeAppendStorage _fakeStorage => (FakeAppendStorage)ServiceProvider.GetService<IAppendStorage>(); // ServiceProvider.GetService<FakeAppendStorage>() ?? (FakeAppendStorage)ServiceProvider.GetService<IAppendStorage<Event, Guid>>() ?? (FakeAppendStorage)ServiceProvider.GetService<IAppendStorage>();
	ISynqraCollection<MyPocoTask> _tasks => _sut.GetCollection<MyPocoTask>();

	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		HostBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default); // im not sure yet, context or options
		HostBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions); // im not sure yet, context or options

		HostBuilder.AddTypeMetadataProvider([
			typeof(DemoModel),
			typeof(MyPocoTask),
			typeof(Command),
			typeof(CreateObjectCommand),
			typeof(ChangeObjectPropertyCommand),
			typeof(Item),
		]);
		var q1 = new Item(); // must register polimorfic before serializaiton
		var q2 = new CreateObjectCommand(); // must register polimorfic before serializaiton
		var q3 = new ChangeObjectPropertyCommand() { PropertyName = "q" }; // must register polimorfic before serializaiton

		HostBuilder.Services.AddSingleton<FakeAppendStorage>();
		HostBuilder.Services.AddSingleton<IAppendStorage<Event, Guid>>(sp => sp.GetRequiredService<FakeAppendStorage>());
		HostBuilder.Services.AddSingleton<IAppendStorage>(sp => sp.GetRequiredService<FakeAppendStorage>());

		HostBuilder.Services.AddSingleton<ISBXSerializerFactory>(new SBXSerializerFactory(() =>
		{
			var ser = new SBXSerializer();
			ser.Map(100, -1, typeof(MyPocoTask));
			ser.Map(101, 3000.0, typeof(Item));
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Snapshot();
			return ser;
		}));

		// HostBuilder.AddJsonLinesStorage();

		// var _fileName = string.Empty;
		// Configuration["Storage:JsonLinesStorage:FileName"] = _fileName = $"TestData/data_{Guid.NewGuid():N}_[TypeName].jsonl";
		// Directory.CreateDirectory(Path.GetDirectoryName(_fileName));
	}

	public void Reopen()
	{
		var fakeAppendStorage = ServiceProvider.GetRequiredService<FakeAppendStorage>();
		Restart();
		ServiceCollection.AddSingleton(fakeAppendStorage);
	}

	[Test]
	public async Task Should_00_have_proper_container()
	{
		Console.WriteLine(_sut);
	}

	[Test]
	public async Task Should_20_emit_command_by_setting_property()
	{
		Console.WriteLine("v2");
		// HostBuilder.AddJsonLinesStorage();
		// HostBuilder.AddSynqraStoreContext();

		var model = new DemoModel();
		_sut.GetCollection<DemoModel>().Add(model);
		await Assert.That(model.Name).IsNotEqualTo("TestName");

		model.Name = "TestName"; // this should emit a command and broadcast it

		var commands = _sut.GetCollection<Command>().ToArray();

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
	public async Task Should_10_create_object_by_adding_to_list()
	{
		foreach (var item in ServiceCollection.Skip(_origServiceCount))
		{
			Console.WriteLine($"{item.ServiceType.Name} '{item.ServiceKey}' = {(item.IsKeyedService ? item.KeyedImplementationInstance : item.ImplementationInstance)}");
		}
		Console.WriteLine("/////////");
		foreach (var item in _tasks)
		{
			Console.WriteLine(item);
		}
		Console.WriteLine("=======================");
		var t = new MyPocoTask { Subject = "Test Task" };
		_tasks.Add(t);

		var events = _fakeStorage.Items.OfType<Event>().ToArray();
		foreach (var item in events)
		{
			Console.WriteLine(JsonSerializer.Serialize(item, _jsonSerializerOptions));
		}

		Console.WriteLine("=======================");
		foreach (var item in _tasks)
		{
			Console.WriteLine(item .Subject+ " " + item.Id + " " + _sut.GetId(item));
		}
		Console.WriteLine("=======================");

		// objects
		await Assert.That(_tasks).HasCount(1);
		await Assert.That(_tasks.First().Subject).IsEqualTo("Test Task");
		await Assert.That(_tasks.First()).IsEquivalentTo(t);
		await Assert.That(ReferenceEquals(_tasks.First(), t)).IsTrue();

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
		await Assert.That(tasks.First().Subject).IsEqualTo("Test Task");

		// reopen
		Reopen();
		if (_sut is InMemoryProjection imp)
		{
			await imp.LoadStateAsync();
		}

		//var bt = (StateManagementTests)Activator.CreateInstance(GetType());
		//bt.ServiceCollection.AddSingleton(_fakeStorage);
		//bt.ServiceCollection.AddSingleton<IAppendStorage>(_fakeStorage);
		//var reopened = bt.ServiceProvider.GetRequiredService<IProjection>();

		tasks = _sut.GetCollection<MyPocoTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks.First().Subject).IsEqualTo("Test Task");
	}

	[Test]
	public async Task Should_25_change_object()
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
		await Assert.That(_tasks.First().Subject).IsEqualTo("123");

		// reopen
		Reopen();
		if (_sut is InMemoryProjection imp)
		{
			await imp.LoadStateAsync();
		}

		var tasks = _sut.GetCollection<MyPocoTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks.First().Subject).IsEqualTo("123");
	}

	[Test]
	public async Task Should_30_instantiate_collection_by_ctor()
	{
		var tasks = _sut.GetCollection<MyPocoTask>();
	}

	[Test]
	public async Task Should_30_instantiate_collection_by_type()
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
	public Guid Id { get; set; }
	public string Subject { get; set; }
}

public class FakeAppendStorage : FakeAppendStorage<Event, Guid>, IAppendStorage
{
}

public class FakeAppendStorage<T, TKey> : IAppendStorage<T, TKey>
	where T : class
	// where T : IIdentifiable<TKey>
{
	public List<T> Items { get; } = new List<T>();

	public Task AppendAsync(T item, CancellationToken cancellationToken = default)
	{
		Items.Add(item);
		return Task.CompletedTask;
	}

	public Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
	{
		Items.AddRange(items);
		return Task.CompletedTask;
	}

	public void Dispose()
	{
	}

	public ValueTask DisposeAsync()
	{
		return default;
	}

	public Task FlushAsync(CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public async IAsyncEnumerable<T> GetAllAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		foreach (T item in Items)
		{
			// if (from == null || item is IEvent e && e.Id > from)
			{
				yield return item;
			}
		}
	}

	public Task<T> GetAsync(TKey key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	public Task<string> TestAsync(string input)
	{
		throw new NotImplementedException();
	}
}

[SynqraModel]
[Schema(2026.155, "1 Name string Prprpr string")]
[Schema(3000.0, "1 Name string Prprpr string")]
public partial class DemoModel
{
	public Guid Id => ((IBindableModel)this).Store.GetId(this);

	public partial string Name { get; set; }
	public partial string Prprpr { get; set; }
}

[SynqraModel]
[Schema(2026.164, "1 Key string Title string")]
public partial class StorableModel
{
	public partial string Key { get; set; }

	public partial string Title { get; set; }
}

[SynqraModel]
[Schema(2026.164, "1 Title string")]
public partial class CollectionElementModel
{
	public Guid ObjectId => ((IBindableModel)this).Store.GetId(this);
	[JsonIgnore]
	public Guid CollectionId { get; set; }

	public partial string Title { get; set; }
}
