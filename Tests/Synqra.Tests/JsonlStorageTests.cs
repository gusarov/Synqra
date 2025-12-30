using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Synqra.Storage.Jsonl;
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

public class TestItem //: IIdentifiable<int>
{
	public int Id { get; set; }
	public string Name { get; set; }
}

[NotInParallel]
public class StorageTests<T, TKey> : BaseTest
	//where T //: IIdentifiable<TKey>
{
	private IStorage<T, TKey>? __storage;

	protected IStorage<T, TKey> _storage => __storage ?? (__storage = ServiceProvider.GetRequiredService<IStorage<T, TKey>>());

	protected string _fileName;

	public void SetupCore(string? fileName = null)
	{
		HostBuilder.AddJsonLinesStorage<TestItem, int>();
		HostBuilder.AddJsonLinesStorage<Event, Guid>();
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
		var fix = new StorageTests<T, TKey>();
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
	public async Task Should_register_quickly()
	{
		Configuration["Storage:JsonLinesStorage:FileName"] = "test1";

		var cnt = ServiceCollection.Count;

		HostBuilder.AddJsonLinesStorageCore();

		await Assert.That(MeasureOps(() =>
		{
			HostBuilder.AddJsonLinesStorageCore();
			/*
			for (int i = ServiceCollection.Count - 1; i >= cnt; i--)
			{
				ServiceCollection.RemoveAt(i);
			}
			*/
		})).IsGreaterThan(20_000_000);

		await Assert.That(ServiceCollection.Count).IsEqualTo(cnt + 2);

		var cfg = ServiceProvider.GetRequiredService<IOptions<StorageExtensions.JsonLinesStorageConfig>>();
		await Assert.That(cfg.Value.FileName).IsEqualTo("test1");
	}
}

[InheritsTests]
public class TestItemJsonlStorageTests : StorageTests<TestItem, int>
{
	[Test]
	public async Task Should_allow_append_and_read_items()
	{
		await _storage.AppendAsync(new TestItem { Id = 1, Name = "Test Item 1", });
		await _storage.AppendAsync(new TestItem { Id = 2, Name = "Test Item 2", });

		await ReopenAsync();

		var items = _storage.GetAll().ToBlockingEnumerable().ToList();

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
{"id":1,"name":"Test Item 1"}
{"id":2,"name":"Test Item 2"}

""".Replace("\r\n", "\n"));
	}

	[Test]
	// [Explicit]
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
	// [Explicit]
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

		var iterator = _storage.GetAll().GetAsyncEnumerator();

		await Assert.That(await iterator.MoveNextAsync()).IsTrue();
		await Assert.That(await iterator.MoveNextAsync()).IsFalse(); // reached the end
		await Assert.That(await iterator.MoveNextAsync()).IsFalse(); // reached the end

		await _storage.AppendAsync(new TestItem { Id = 0, Name = "For iterator" });

		await Assert.That(await iterator.MoveNextAsync()).IsTrue(); // continued!!
		await Assert.That(await iterator.MoveNextAsync()).IsFalse(); // reached the end again
	}
}

[InheritsTests]
public class EventsJsonlStorageTests : StorageTests<Event, Guid>
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

		_storage.Dispose();
		await Assert.That(FileReadAllText(_fileName).NormalizeNewLines()).IsEqualTo("""
{"Synqra.Storage.Jsonl":"0.1","rootItemType":"Synqra.Event"}
{"_t":"ObjectCreatedEvent","targetId":"00000000-0000-0000-0000-000000000000","targetTypeId":"00000000-0000-0000-0000-000000000000","collectionId":"00000000-0000-0000-0000-000000000000","eventId":"00000000-0000-0000-0000-000000000000","commandId":"00000000-0000-0000-0000-000000000000"}

""".NormalizeNewLines());
	}
}
