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

namespace Synqra.Tests;

using IAppendStorage = IAppendStorage<Event, Guid>;

[InheritsTests]
public class InMemoryStateManageementTests : StateManagementTests
{
	protected override void Registration(IHostApplicationBuilder hostApplicationBuilder)
	{
		hostApplicationBuilder.AddInMemorySynqraStore();
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
	FakeAppendStorage _fakeStorage => ServiceProvider.GetService<FakeAppendStorage>() ?? (FakeAppendStorage)ServiceProvider.GetService<IAppendStorage<Event, Guid>>() ?? (FakeAppendStorage)ServiceProvider.GetService<IAppendStorage>();
	ISynqraCollection<MyPocoTask> _tasks => _sut.GetCollection<MyPocoTask>();

	public StateManagementTests()
	{
		Registration(HostBuilder);
		HostBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default); // im not sure yet, context or options
		HostBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions); // im not sure yet, context or options

		HostBuilder.Services.AddSingleton<FakeAppendStorage>();
		// HostBuilder.Services.AddSingleton(typeof(IStorage<,>), typeof(FakeStorage<,>));
		// HostBuilder.Services.AddSingleton<IStorage<Event, Guid>, FakeStorage<Event, Guid>>();
		HostBuilder.Services.AddSingleton<IAppendStorage<Event, Guid>>(sp => sp.GetRequiredService<FakeAppendStorage>());
		HostBuilder.Services.AddSingleton<IAppendStorage>(sp => sp.GetRequiredService<FakeAppendStorage>());

		HostBuilder.Services.AddSingleton<ISBXSerializerFactory>(new SBXSerializerFactory(() =>
		{
			var ser = new SBXSerializer();
			ser.Map(100, -1, typeof(MyPocoTask));
			ser.Snapshot();
			return ser;
		}));

		// HostBuilder.AddJsonLinesStorage();

		// var _fileName = string.Empty;
		// Configuration["Storage:JsonLinesStorage:FileName"] = _fileName = $"TestData/data_{Guid.NewGuid():N}_[TypeName].jsonl";
		// Directory.CreateDirectory(Path.GetDirectoryName(_fileName));
	}

	protected abstract void Registration(IHostApplicationBuilder hostApplicationBuilder);

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
		var bt = (StateManagementTests)Activator.CreateInstance(GetType());
		bt.ServiceCollection.AddSingleton(_fakeStorage);
		bt.ServiceCollection.AddSingleton<IAppendStorage>(_fakeStorage);
		var reopened = bt.ServiceProvider.GetRequiredService<IProjection>();

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
		var bt = (StateManagementTests)Activator.CreateInstance(GetType());
		bt.ServiceCollection.AddSingleton(_fakeStorage);
		bt.ServiceCollection.AddSingleton<IAppendStorage>(_fakeStorage);
		var reopened = bt.ServiceProvider.GetRequiredService<IObjectStore>();
		await ((InMemoryProjection)reopened).LoadStateAsync();

		var tasks = reopened.GetCollection<MyPocoTask>();
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
	public Guid Id { get; set; }
	public string Subject { get; set; }
}

public class FakeAppendStorage : FakeAppendStorage<Event, Guid>, IAppendStorage
{
}

public class FakeAppendStorage<T, TKey> : IAppendStorage<T, TKey>
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

	public Task<string> TestAsync(string input)
	{
		throw new NotImplementedException();
	}
}

[SynqraModel]
[Schema(2026.155, "1 Name string Prprpr string")]
[Schema(2026.156, "1 Name string Prprpr string")]
public partial class DemoModel
{
	public Guid Id => ((IBindableModel)this).Store.GetId(this);

	public partial string Name { get; set; }
	public partial string Prprpr { get; set; }
}
