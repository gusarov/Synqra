using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage;
using Synqra.AppendStorage.File;
using Synqra.AppendStorage.JsonLines;
using Synqra.BinarySerializer;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;
using static Synqra.AppendStorage.JsonLines.AppendStorageJsonLinesExtensions;

namespace Synqra.Tests;

public class TestItem //: IIdentifiable<int>
{
	public int Id { get; set; }
	public string Name { get; set; }
}

[InheritsTests]
public class JsonAppendStorageTests : AppendStorageTests
{
	[Before(Test)]
	public void Setup()
	{
		HostBuilder.AddAppendStorageJsonLines<TestItem, int>();
		HostBuilder.AddAppendStorageJsonLines<Event, Guid>();
		ServiceCollection.AddSingleton(SampleJsonSerializerContext.Default);
		ServiceCollection.AddSingleton(SampleJsonSerializerContext.DefaultOptions);

		Configuration["Storage:JsonLinesStorage:FileName"] = CreateTestFileName("[TypeName]");
	}
}

[InheritsTests]
public class FileAppendStorageTests : AppendStorageTests
{
	[Before(Test)]
	public void Setup()
	{
		// HostBuilder.AddAppendStorageFile<TestItem, int>(x => x.Id);
		HostBuilder.AddAppendStorageFile<Event, Guid>(x => x.EventId);
		HostBuilder.Services.AddSingleton<ISBXSerializerFactory>(new SBXSerializerFactory(() =>
		{
			var ser = new SBXSerializer();
			ser.Map(100, typeof(Event));
			ser.Map(101, typeof(ObjectCreatedEvent));
			ser.Snapshot();
			return ser;
		}));
		Configuration["Storage:FileStorage:Folder"] = CreateTestFileName("store") + Path.DirectorySeparatorChar;
	}

}

[NotInParallel]
public abstract class AppendStorageTests : BaseTest
//where T //: IIdentifiable<TKey>
{
	// class


	protected IAppendStorage<T, TKey> Get<T, TKey>()
	{
		return ServiceProvider.GetRequiredService<IAppendStorage<T, TKey>>();
	}

	protected IAppendStorage<T, Guid> Get<T>()
	{
		return ServiceProvider.GetRequiredService<IAppendStorage<T, Guid>>();
	}

	[Test]
	public async Task Should_00_be_empty()
	{
		var storage = Get<Event>();
		await Assert.That(storage.GetAllAsync().ToBlockingEnumerable().Count()).IsEqualTo(0);
	}

	[Test]
	public async Task Should_10_add_and_read()
	{
		var storage = Get<Event>();
		var key = Guid.NewGuid();
		var item = new ObjectCreatedEvent
		{
			CollectionId = Guid.NewGuid(),
			CommandId = Guid.NewGuid(),
			EventId = key,
			TargetId = Guid.NewGuid(),
			TargetTypeId = Guid.NewGuid(),
		};
		await storage.AppendAsync(item);

		var items = storage.GetAllAsync().ToBlockingEnumerable().ToArray();
		await Assert.That(items.Count()).IsEqualTo(1);
		var theItem = items[0];
		await Assert.That(theItem).IsEquivalentTo(item);
	}

	[Test]
	public async Task Should_11_add_and_read_two_objects()
	{
		var storage = Get<Event>();

		var guidSpace = new GuidExtensions.Generator();

		var key1 = guidSpace.CreateVersion7();
		var item1 = new ObjectCreatedEvent
		{
			CollectionId = new Guid("00000001-0001-8000-8000-c0de11dae333"),
			CommandId = new Guid("00000001-0002-8000-8000-c0de11dae333"),
			EventId = key1,
			TargetId = new Guid("00000001-0003-8000-8000-c0de11dae333"),
			TargetTypeId = new Guid("00000001-0004-8000-8000-c0de11dae333"),
		};
		await storage.AppendAsync(item1);

		var key2 = guidSpace.CreateVersion7();
		var item2 = new ObjectCreatedEvent
		{
			CollectionId = new Guid("00000002-0001-8000-8000-c0de11dae333"),
			CommandId = new Guid("00000002-0002-8000-8000-c0de11dae333"),
			EventId = key2,
			TargetId = new Guid("00000002-0003-8000-8000-c0de11dae333"),
			TargetTypeId = new Guid("00000002-0004-8000-8000-c0de11dae333"),
		};
		await storage.AppendAsync(item2);

		var items = storage.GetAllAsync().ToBlockingEnumerable().ToArray();
		await Assert.That(items.Count()).IsEqualTo(2);
		var theItem1 = items[0];
		await Assert.That(theItem1).IsEquivalentTo(item1);
		await Assert.That(theItem1).IsEquivalentTo(item1);
		var theItem2 = items[1];
		await Assert.That(theItem2).IsEquivalentTo(item2);
		await Assert.That(theItem2).IsEquivalentTo(item2);
	}
}

[NotInParallel]
public abstract class JsonAppendStorageTests<T, TKey> : BaseTest
	//where T //: IIdentifiable<TKey>
{
	// class 

	/*
	protected IAppendStorage<T, TKey> Get<T, TKey>()
	{
		return ServiceProvider.GetRequiredService<IAppendStorage<T, TKey>>();
	}

	protected IAppendStorage<T, Guid> Get<T>()
	{
		return ServiceProvider.GetRequiredService<IAppendStorage<T, Guid>>();
	}
	*/

	private IAppendStorage<T, TKey>? __storage;

	protected IAppendStorage<T, TKey> _storage => __storage ?? (__storage = ServiceProvider.GetRequiredService<IAppendStorage<T, TKey>>());

	protected string _fileName;

	public void SetupCore(string? fileName = null)
	{
		HostBuilder.AddAppendStorageJsonLines<TestItem, int>();
		HostBuilder.AddAppendStorageJsonLines<Event, Guid>();
		ServiceCollection.AddSingleton(SampleJsonSerializerContext.Default);
		ServiceCollection.AddSingleton(SampleJsonSerializerContext.DefaultOptions);

		Configuration["Storage:JsonLinesStorage:FileName"] = _fileName = fileName ?? CreateTestFileName("data.jsonl");
	}

	[Before(Test)]
	public void Setup()
	{
		SetupCore();
	}

	[After(Test)]
	public void After()
	{
		 _storage?.Dispose();
	}

	protected async Task ReopenAsync()
	{
		_storage.Dispose();
		var fix = (JsonAppendStorageTests<T, TKey>)Activator.CreateInstance(GetType());
		fix.SetupCore(_fileName);
		__storage = fix._storage;
	}
}

[InheritsTests]
public class JsonLinesStorageRegistrationPerformance : BaseTest
{
	[Test]
	[Category("Performance")]
	[Property("CI", "false")]
	[Explicit]
	public async Task Should_register_quickly()
	{
		Configuration["Storage:JsonLinesStorage:FileName"] = "test1";

		var cnt = ServiceCollection.Count;

		HostBuilder.AddAppendStorageJsonLinesCore();

		await Assert.That(MeasureOps(() =>
		{
			HostBuilder.AddAppendStorageJsonLinesCore();
			/*
			for (int i = ServiceCollection.Count - 1; i >= cnt; i--)
			{
				ServiceCollection.RemoveAt(i);
			}
			*/
		})).IsGreaterThan(20_000_000);

		await Assert.That(ServiceCollection.Count).IsEqualTo(cnt + 2);

		var cfg = ServiceProvider.GetRequiredService<IOptions<JsonLinesStorageConfig>>();
		await Assert.That(cfg.Value.FileName).IsEqualTo("test1");
	}
}

[InheritsTests]
public class TestItemJsonlStorageTests : JsonAppendStorageTests<TestItem, int>
{
	[Test]
	public async Task Should_allow_append_and_read_items()
	{
		await _storage.AppendAsync(new TestItem { Id = 1, Name = "Test Item 1", });
		await _storage.AppendAsync(new TestItem { Id = 2, Name = "Test Item 2", });

		await ReopenAsync();

		var items = _storage.GetAllAsync().ToBlockingEnumerable().ToList();

		await Assert.That(items).HasCount(2);

		await Assert.That(items[0].Id).IsEqualTo(1);
		await Assert.That(items[0].Name).IsEqualTo("Test Item 1");
		await Assert.That(items[1].Id).IsEqualTo(2);
		await Assert.That(items[1].Name).IsEqualTo("Test Item 2");
	}

	[Test]
	public async Task Should_store_objects_as_jsonl()
	{
		await _storage.AppendAsync(new TestItem { Id = 1, Name = "Test Item 1", });
		await _storage.AppendAsync(new TestItem { Id = 2, Name = "Test Item 2", });

		_storage.Dispose();
		await Assert.That(FileReadAllText(_fileName).Replace("\r\n", "\n")).IsEqualTo("""
{"Synqra.Storage.Jsonl":"0.1","rootItemType":"Synqra.Tests.TestItem"}
{"Id":1,"Name":"Test Item 1"}
{"Id":2,"Name":"Test Item 2"}

""".Replace("\r\n", "\n"));
	}

	[Test]
	[Explicit]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_write_quickly()
	{
		int id = 0;
		MeasurePerformance(async () => {
			await _storage.AppendAsync(new TestItem { Id = ++id, Name = "Test Item " + id });
		});
	}

	[Test]
	[Explicit]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_write_quickly2()
	{
		var item = new TestItem { Id = 1, Name = "Test Item 1", };
		MeasurePerformance(async () => {
			await _storage.AppendAsync(item);
		});
	}

	[Test]
	public async Task Should_continue_reading_with_same_iterator()
	{
		await _storage.AppendAsync(new TestItem { Id = 0, Name = "For iterator", });

		var iterator = _storage.GetAllAsync().GetAsyncEnumerator();

		await Assert.That(await iterator.MoveNextAsync()).IsTrue();
		await Assert.That(await iterator.MoveNextAsync()).IsFalse(); // reached the end
		await Assert.That(await iterator.MoveNextAsync()).IsFalse(); // reached the end

		await _storage.AppendAsync(new TestItem { Id = 0, Name = "For iterator" });

		await Assert.That(await iterator.MoveNextAsync()).IsTrue(); // continued!!
		await Assert.That(await iterator.MoveNextAsync()).IsFalse(); // reached the end again
	}
}

[InheritsTests]
public class EventsJsonlStorageTests : JsonAppendStorageTests<Event, Guid>
{
	[Test]
	public async Task Should_store_polimorfic_as_jsonl()
	{
		await _storage.AppendAsync(new ObjectCreatedEvent
		{
			CollectionId = default,
			CommandId = default,
			EventId = default,
			TargetId = default,
			TargetTypeId = default,
		});

		(_storage as IDisposable)?.Dispose();
		await Assert.That(FileReadAllText(_fileName).NormalizeNewLines()).IsEqualTo("""
{"Synqra.Storage.Jsonl":"0.1","rootItemType":"Synqra.Event"}
{"_t":"ObjectCreatedEvent","TargetId":"00000000-0000-0000-0000-000000000000","TargetTypeId":"00000000-0000-0000-0000-000000000000","CollectionId":"00000000-0000-0000-0000-000000000000","EventId":"00000000-0000-0000-0000-000000000000","CommandId":"00000000-0000-0000-0000-000000000000"}

""".NormalizeNewLines());
	}
}
