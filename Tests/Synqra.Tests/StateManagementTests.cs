using Microsoft.Extensions.DependencyInjection;
using Synqra;
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

namespace Synqra.Tests;

// [NotInParallel]
public class GuidExtensionsTests
{
	[Test]
	public async Task Should_01_Create_Version5_Guid()
	{
		var guid1 = Synqra.GuidExtensions.CreateVersion5("Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsNotEqualTo(default);
	}

	[Test]
	public async Task Should_01_Create_Version3_Guid()
	{
		var guid1 = Synqra.GuidExtensions.CreateVersion3("Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsNotEqualTo(default);
	}

	[Test]
	public async Task Should_02_Create_Version5_Guid2()
	{
		var guid1 = Synqra.GuidExtensions.CreateVersion5("Test");
		var guid2 = Synqra.GuidExtensions.CreateVersion5("Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsEqualTo(guid2);
		await Assert.That(guid1).IsNotEqualTo(GuidExtensions.CreateVersion3("Test"));
	}

	[Test]
	public async Task Should_02_Create_Version3_Guid2()
	{
		var guid1 = Synqra.GuidExtensions.CreateVersion3("Test");
		var guid2 = Synqra.GuidExtensions.CreateVersion3("Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsEqualTo(guid2);
		await Assert.That(guid1).IsNotEqualTo(GuidExtensions.CreateVersion5("Test"));
	}

	[Test]
	[Explicit]
	public async Task Should_create_v5_Guid_quickly()
	{
		await Assert.That(PerformanceTestUtils.MeasureOps(async () =>
		{
			GuidExtensions.CreateVersion5("Test");
		})).IsGreaterThan(500_000);
	}

	[Test]
	[Explicit]
	public async Task Should_create_v3_Guid_quickly()
	{
		await Assert.That(PerformanceTestUtils.MeasureOps(async () =>
		{
			GuidExtensions.CreateVersion3("Test");
		})).IsGreaterThan(500_000);
	}

	[Test]
	[Explicit]
	public async Task Should_create_v5_Guid_random_quickly()
	{
		var buf = new byte[16];
		await Assert.That(PerformanceTestUtils.MeasureOps(async () =>
		{
			Random.Shared.NextBytes(buf);
			GuidExtensions.CreateVersion5(buf);
		})).IsGreaterThan(300_000);
	}

	[Test]
	[Explicit]
	public async Task Should_create_v3_Guid_random_quickly()
	{
		var buf = new byte[16];
		await Assert.That(PerformanceTestUtils.MeasureOps(async () =>
		{
			Random.Shared.NextBytes(buf);
			GuidExtensions.CreateVersion3(buf);
		})).IsGreaterThan(300_000);
	}

	[Test]
	[Explicit]
	public async Task Should_create_v5_Guid_super_long()
	{
		var buf = new byte[16 * 1024];
		Random.Shared.NextBytes(buf);
		var perf = PerformanceTestUtils.MeasurePerformance(async () =>
		{
			GuidExtensions.CreateVersion5(buf);
		});
		await Assert.That(perf.OperationsPerSecond).IsGreaterThan(10_000);
	}

	[Test]
	[Explicit]
	public async Task Should_create_v3_Guid_super_long()
	{
		var buf = new byte[16 * 1024];
		Random.Shared.NextBytes(buf);
		var perf = PerformanceTestUtils.MeasurePerformance(async () =>
		{
			GuidExtensions.CreateVersion3(buf);
		});
		await Assert.That(perf.OperationsPerSecond).IsGreaterThan(10_000);
	}
}

public class StateManagementTests : BaseTest
{
	ISynqraStoreContext _sut => ServiceProvider.GetRequiredService<ISynqraStoreContext>();
	FakeStorage _fakeStorage => ServiceProvider.GetRequiredService<FakeStorage>();
	IStoreCollection<MyTask> _tasks => _sut.Get<MyTask>();

	public StateManagementTests()
	{
		HostBuilder.AddSynqraStoreContext();
		HostBuilder.Services.AddSingleton<JsonSerializerContext>(TestJsonSerializerContext.Default); // im not sure yet, context or options
		HostBuilder.Services.AddSingleton<JsonSerializerOptions>(TestJsonSerializerContext.Default.Options); // im not sure yet, context or options
		HostBuilder.Services.AddSingleton<FakeStorage>();
		HostBuilder.Services.AddSingleton<IStorage>(sp => sp.GetRequiredService<FakeStorage>());
		// HostBuilder.AddJsonLinesStorage();

		// var _fileName = string.Empty;
		// Configuration["JsonLinesStorage:FileName"] = _fileName = $"TestData/data_{Guid.NewGuid():N}_[TypeName].jsonl";
		// Directory.CreateDirectory(Path.GetDirectoryName(_fileName));
	}

	[Test]
	public async Task Should_create_object()
	{
		var t = new MyTask { Subject = "Test Task" };
		_tasks.Add(t);

		var events = _fakeStorage.Items.OfType<Event>().ToArray();
		await Assert.That(events).HasCount(1);
		await Assert.That(events[0]).IsTypeOf<ObjectCreatedEvent>();
		// await Assert.That(events[1]).IsTypeOf<ObjectPropertyChangedEvent>();

		await Assert.That(_tasks).HasCount(1);
		await Assert.That(_tasks[0].Subject).IsEqualTo("Test Task");
		await Assert.That(_tasks[0]).IsEquivalentTo(t);
		await Assert.That(ReferenceEquals(_tasks[0], t)).IsTrue();

		// reopen
		var bt = new StateManagementTests();
		bt.ServiceCollection.AddSingleton<FakeStorage>(_fakeStorage);
		bt.ServiceCollection.AddSingleton<IStorage>(_fakeStorage);
		var reopened = bt.ServiceProvider.GetRequiredService<ISynqraStoreContext>();

		var tasks = _sut.Get<MyTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks[0].Subject).IsEqualTo("Test Task");
	}

	[Test]
	public async Task Should_change_object()
	{
		var t = new MyTask { Subject = "Test Task" };
		_tasks.Add(t);

		using (_tasks.PocoTracker(t))
		{
			t.Subject = "123"; // There should be event driven mode that helps to track the changes, but POCO also must work, so need snapshotting
		}

		var events = _fakeStorage.Items.OfType<Event>().ToArray();
		await Assert.That(events).HasCount(2);
		await Assert.That(events[1]).IsTypeOf<ObjectPropertyChangedEvent>();

		await Assert.That(_tasks).HasCount(1);
		await Assert.That(_tasks[0].Subject).IsEqualTo("123");

		// reopen
		var bt = new StateManagementTests();
		bt.ServiceCollection.AddSingleton<FakeStorage>(_fakeStorage);
		bt.ServiceCollection.AddSingleton<IStorage>(_fakeStorage);
		var reopened = bt.ServiceProvider.GetRequiredService<ISynqraStoreContext>();

		var tasks = _sut.Get<MyTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks[0].Subject).IsEqualTo("123");
	}

	[Test]
	public async Task Should_instantiate_collection_by_ctor()
	{
		var tasks = _sut.Get<MyTask>();
	}

	[Test]
	public async Task Should_instantiate_collection_by_type()
	{
		var tasks = _sut.Get(typeof(MyTask));
	}
}

public class MyTask
{
	public string Subject { get; set; } = string.Empty;
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
