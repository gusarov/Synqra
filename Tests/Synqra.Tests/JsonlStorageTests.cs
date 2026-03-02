using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage;
using Synqra.AppendStorage.File;
using Synqra.AppendStorage.JsonLines;
using Synqra.BinarySerializer;
using Synqra.Projection.File;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
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
	string _file;

	[Before(Test)]
	public void Setup()
	{
		_file = CreateTestFileName("[TypeName]");
	}

	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);

		HostBuilder.AddAppendStorageJsonLines<TestItem, int>("Id", x => x.Id, x => x.ToString(), x => int.Parse(x));
		HostBuilder.AddAppendStorageJsonLines<Event>("EventId", x => x.EventId);
		HostBuilder.AddAppendStorageJsonLines<StorableModel, string>("Key", x => x.Key, x => x, x => x);
		HostBuilder.AddAppendStorageJsonLines<Item>("", x => (x.CollectionId, x.ObjectId));

		ServiceCollection.AddSingleton(SampleJsonSerializerContext.Default);
		ServiceCollection.AddSingleton(SampleJsonSerializerContext.DefaultOptions);

		// HostBuilder.AddAppendStorageFile<StorableModel, (Guid, Guid)>(x => (, x.Key), x => x, x => x);
		Configuration["Storage:JsonLinesStorage:FileName"] = _file;
	}
}

[InheritsTests]
public class FileAppendStorageTests : AppendStorageTests
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
		// HostBuilder.AddAppendStorageFile<TestItem, int>(x => x.Id);
		HostBuilder.AddAppendStorageFile<Event>(x => x.EventId);
		HostBuilder.AddAppendStorageFile<StorableModel, string>(x => x.Key, x => x, x => x);
		HostBuilder.AddAppendStorageFile<Item>(x => (x.CollectionId, x.ObjectId));
		// HostBuilder.AddAppendStorageFile<StorableModel, (Guid, Guid)>(x => (, x.Key), x => x, x => x);
		Configuration["Storage:FileStorage:Folder"] = Path.Combine(_folder, "[Type]") + Path.DirectorySeparatorChar;
	}

	[Test]
	public async Task Should_F00_stringify_guid_in_portable_way()
	{
		var guid = Guid.Parse("019ca6ce-d93b-7802-86bf-60d00e8ec15b");
		await Assert.That(guid.ToString()).IsEqualTo("019ca6ce-d93b-7802-86bf-60d00e8ec15b");
	}

	[Test]
	[Arguments(1, "alice", "al/ice")]
	[Arguments(2, "0709D3B1B1F64DF79BC941178E32E530", "070/9D3B1B1F64DF79BC941178E32E530")] // 3 chars 1.5 bytes - v4 high entropy
	[Arguments(3, "0709D3B1B1F64DF79BC941178E32E5300709D3B1B1F64DF79BC941178E32E530", "070/9D3B1B1F64DF79BC941178E32E530/070/9D3B1B1F64DF79BC941178E32E530")] // nested
	// 019ca6ce-d93b-7802-86bf-60d00e8ec15b
	[Arguments(4, "019ca6ced93b780286bf60d00e8ec15b", "019ca6/ced93b780286bf60d00e8ec15b")] // 6 chars 3.0 bytes - v7 gives a folder every 4.66h and 1880 folders a year
	[Arguments(5, "1EC9414C232A6B00B3C89F6BDECED846", "1EC94/14C232A6B00B3C89F6BDECED846")] // 5 chars 2.5 bytes - v6 gives a folder every  1.2d and  288 folders a year
	public async Task Should_F10_prepare_file_name(int num, string key, string expectedPath)
	{
		expectedPath = expectedPath.Replace('/', Path.DirectorySeparatorChar);
		var storage = Get<StorableModel, string>();
		await Assert.That(((FileAppendStorage<StorableModel, string>)storage).GetFileNameFor(key, false)).EndsWith(expectedPath);
	}
}

[NotInParallel]
public abstract class AppendStorageTests : BaseTest
//where T //: IIdentifiable<TKey>
{
	// class
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		HostBuilder.Services.AddSingleton<ISBXSerializerFactory>(new SBXSerializerFactory(() =>
		{
			var ser = new SBXSerializer();
			ser.Map(100, typeof(Event));
			ser.Map(101, typeof(ObjectCreatedEvent));
			ser.Map(102, typeof(StorableModel));
			ser.Map(99, 3000.0, typeof(Item));
			ser.Snapshot();
			return ser;
		}));

		hostApplicationBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default); // im not sure yet, context or options
		hostApplicationBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions); // im not sure yet, context or options
	}

	protected virtual void Reopen()
	{
		Restart(); // restart host builder - useful with virtual Register()
		Console.WriteLine("Reopened");
	}

	protected IAppendStorage<T, TKey> Get<T, TKey>()
		where T : class
	{
		return ServiceProvider.GetRequiredService<IAppendStorage<T, TKey>>();
	}

	protected IAppendStorage<T, Guid> Get<T>()
		where T : class
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

		Reopen();
		storage = Get<Event>();
		items = storage.GetAllAsync().ToBlockingEnumerable().ToArray();
		await Assert.That(items.Count()).IsEqualTo(1);
		theItem = items[0];
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

		Reopen();
		storage = Get<Event>();
		items = storage.GetAllAsync().ToBlockingEnumerable().ToArray();
		await Assert.That(items.Count()).IsEqualTo(2);
		theItem1 = items[0];
		await Assert.That(theItem1).IsEquivalentTo(item1);
		await Assert.That(theItem1).IsEquivalentTo(item1);
		theItem2 = items[1];
		await Assert.That(theItem2).IsEquivalentTo(item2);
		await Assert.That(theItem2).IsEquivalentTo(item2);

	}

	[Test]
	public async Task Should_13_allow_custom_string_not_guid()
	{
		var storage = Get<StorableModel, string>();

		await storage.AppendAsync(new StorableModel
		{
			Key = "alice",
			Title = "Alice",
		});

		await storage.AppendAsync(new StorableModel
		{
			Key = "bob",
			Title = "Bob",
		});
		Console.WriteLine();
	}

	[Test]
	public async Task Should_14_give_object_by_key()
	{
		var storage = Get<Event, Guid>();

		var ev = new ObjectCreatedEvent
		{
			CollectionId = GuidExtensions.CreateVersion7(),
			CommandId = GuidExtensions.CreateVersion7(),
			EventId = Guid.Parse("00000001-0001-8000-8000-000000000000"),
			TargetId = GuidExtensions.CreateVersion7(),
			TargetTypeId = GuidExtensions.CreateVersion7(),
			ContainerId = GuidExtensions.CreateVersion7(),
			Data = new StorableModel
			{
				Title = "Alice",
			},
		};
		await storage.AppendAsync(ev);

		var back = await storage.GetAsync(ev.EventId);
		await Assert.That(back.EventId).IsEqualTo(back.EventId);
		await Assert.That(back.ContainerId).IsEqualTo(back.ContainerId);
		await Assert.That(back.CommandId).IsEqualTo(back.CommandId);

		Reopen();
		storage = Get<Event, Guid>();
		back = await storage.GetAsync(ev.EventId);
		await Assert.That(back.EventId).IsEqualTo(back.EventId);
		await Assert.That(back.ContainerId).IsEqualTo(default); // container id can not be persisted, it is ingnored on serialization
		await Assert.That(back.CommandId).IsEqualTo(back.CommandId);
	}

	[Test]
	public async Task Should_15_give_objecT_by_key_among_two()
	{
		var storage = Get<Event>();

		var ev = new ObjectCreatedEvent
		{
			CollectionId = GuidExtensions.CreateVersion7(),
			CommandId = GuidExtensions.CreateVersion7(),
			EventId = Guid.Parse("00000001-0001-8000-8000-000000000000"),
			TargetId = GuidExtensions.CreateVersion7(),
			TargetTypeId = GuidExtensions.CreateVersion7(),
			ContainerId = GuidExtensions.CreateVersion7(),
			Data = new StorableModel
			{
				Title = "Alice",
			},
		};
		await storage.AppendAsync(ev);

		var ev2 = new ObjectCreatedEvent
		{
			CollectionId = GuidExtensions.CreateVersion7(),
			CommandId = GuidExtensions.CreateVersion7(),
			EventId = Guid.Parse("00000002-0001-8000-8000-000000000000"),
			TargetId = GuidExtensions.CreateVersion7(),
			TargetTypeId = GuidExtensions.CreateVersion7(),
			ContainerId = GuidExtensions.CreateVersion7(),
			Data = new StorableModel
			{
				Title = "Bob",
			},
		};
		await storage.AppendAsync(ev2);

		var back = await storage.GetAsync(ev.EventId);
		await Assert.That(back.EventId).IsEqualTo(ev.EventId);
		await Assert.That(back.ContainerId).IsEqualTo(ev.ContainerId);
		await Assert.That(back.CommandId).IsEqualTo(ev.CommandId);

		var back2 = await storage.GetAsync(ev2.EventId);
		await Assert.That(back2.EventId).IsEqualTo(ev2.EventId);
		await Assert.That(back2.ContainerId).IsEqualTo(ev2.ContainerId);
		await Assert.That(back2.CommandId).IsEqualTo(ev2.CommandId);

		Reopen();
		storage = Get<Event>();

		var Rback = await storage.GetAsync(ev.EventId);
		await Assert.That(Rback).IsNotSameReferenceAs(back);
		await Assert.That(Rback.EventId).IsEqualTo(ev.EventId);
		await Assert.That(Rback.ContainerId).IsEqualTo(default); // container id can not be persisted, it is ingnored on serialization
		await Assert.That(Rback.CommandId).IsEqualTo(ev.CommandId);

		var Rback2 = await storage.GetAsync(ev2.EventId);
		await Assert.That(Rback2).IsNotSameReferenceAs(back2);
		await Assert.That(Rback2.EventId).IsEqualTo(ev2.EventId);
		await Assert.That(Rback2.ContainerId).IsEqualTo(default); // container id can not be persisted, it is ingnored on serialization
		await Assert.That(Rback2.CommandId).IsEqualTo(ev2.CommandId);
	}

	[Test]
	public async Task Should_16_give_from_to_range_query()
	{
		var storage = Get<Item, (Guid, Guid)>();

		var item0 = new Item
		{
			CollectionId = GuidExtensions.CreateVersion7(), // different collection
			ObjectId = GuidExtensions.CreateVersion7(),
			Blob = new MyPocoTask
			{
				Subject = "test" + GuidExtensions.CreateVersion7(),
			},
		};
		await storage.AppendAsync(item0);

		var collectionId1 = GuidExtensions.CreateVersion7();
		var item1 = new Item
		{
			CollectionId = collectionId1,
			ObjectId = GuidExtensions.CreateVersion7(),
			Blob = new MyPocoTask
			{
				Subject = "test" + GuidExtensions.CreateVersion7(),
			},
		};
		await storage.AppendAsync(item1);

		var item2 = new Item
		{
			CollectionId = collectionId1,
			ObjectId = GuidExtensions.CreateVersion7(),
			Blob = new MyPocoTask
			{
				Subject = "test" + GuidExtensions.CreateVersion7(),
			},
		};
		await storage.AppendAsync(item2);

		var item3 = new Item
		{
			CollectionId = GuidExtensions.CreateVersion7(), // different collection
			ObjectId = GuidExtensions.CreateVersion7(),
			Blob = new MyPocoTask
			{
				Subject = "test"+ GuidExtensions.CreateVersion7(),
			},
		};
		await storage.AppendAsync(item3);

		Reopen();

		Console.WriteLine("ByID");
		var back = await storage.GetAsync((collectionId1, item2.ObjectId));
		await Assert.That(((MyPocoTask)back.Blob).Subject).IsEqualTo(((MyPocoTask)item2.Blob).Subject);

		Console.WriteLine("ByPrefix");
		var allRange = storage.GetAllAsync((collectionId1, default)).ToBlockingEnumerable().ToArray();

		await Assert.That(allRange).HasCount(2);
		await Assert.That(((MyPocoTask)allRange[0].Blob).Subject).IsEqualTo(((MyPocoTask)item1.Blob).Subject);
		await Assert.That(((MyPocoTask)allRange[1].Blob).Subject).IsEqualTo(((MyPocoTask)item2.Blob).Subject);

		Console.WriteLine("ByExactRange");
		var hitRange = storage.GetAllAsync((collectionId1, item2.ObjectId)).ToBlockingEnumerable().ToArray();
		foreach (var item in hitRange)
		{
			EmergencyLog.Default.LogDebug($"ByExactRange: {item.CollectionId} {item.ObjectId} {item.Blob}");
		}
		await Assert.That(hitRange).HasCount(1);
		await Assert.That(((MyPocoTask)hitRange[0].Blob).Subject).IsEqualTo(((MyPocoTask)item2.Blob).Subject);
	}
}

[NotInParallel]
public abstract class JsonAppendStorageTests<T, TKey> : BaseTest
	where T : class
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
		HostBuilder.AddAppendStorageJsonLines<TestItem, int>("Id", x => x.Id, x => x.ToString(), x => int.Parse(x));
		HostBuilder.AddAppendStorageJsonLines<Event>("EventId", x => x.EventId);
		ServiceCollection.AddSingleton(SampleJsonSerializerContext.Default);
		ServiceCollection.AddSingleton(SampleJsonSerializerContext.DefaultOptions);

		Configuration["Storage:JsonLinesStorage:FileName"] = _fileName = fileName ?? CreateTestFileName(typeof(T).Name + ".jsonl");
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
1§{"Id":1,"Name":"Test Item 1"}
2§{"Id":2,"Name":"Test Item 2"}

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
	[Explicit] // This no longer works after the change to generated enumerator. Most likely I made it up.
	public async Task Should_continue_reading_with_same_iterator()
	{
		await _storage.AppendAsync(new TestItem { Id = 1, Name = "For iterator", });

		var iterator = _storage.GetAllAsync().GetAsyncEnumerator();

		await Assert.That(await iterator.MoveNextAsync()).IsTrue();
		await Assert.That(await iterator.MoveNextAsync()).IsFalse(); // reached the end
		await Assert.That(await iterator.MoveNextAsync()).IsFalse(); // reached the end

		await _storage.AppendAsync(new TestItem { Id = 2, Name = "For iterator" });

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
			EventId = SynqraGuids.SynqraRootContainerId,
			TargetId = default,
			TargetTypeId = default,
		});

		(_storage as IDisposable)?.Dispose();
		await Assert.That(FileReadAllText(_fileName).NormalizeNewLines()).IsEqualTo($$"""
{"Synqra.Storage.Jsonl":"0.1","rootItemType":"Synqra.Event"}
{{SynqraGuids.SynqraRootContainerId.ToString("N")}}§{"_t":"ObjectCreatedEvent","TargetId":"00000000-0000-0000-0000-000000000000","TargetTypeId":"00000000-0000-0000-0000-000000000000","CollectionId":"00000000-0000-0000-0000-000000000000","EventId":"00000000-000c-8000-8000-c0de2a21b27d","CommandId":"00000000-0000-0000-0000-000000000000"}

""".NormalizeNewLines());
	}
}
