using Microsoft.Extensions.DependencyInjection;
using Synqra.Storage;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using System.Threading.Tasks;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

public class TestItem
{
	public int Id { get; set; }
	public string Name { get; set; }
}

[NotInParallel]
public abstract class StorageTests : BaseTest
{
	protected IStorage _storage;

	protected abstract Task ReopenAsync();

	[After(Test)]
	public void After()
	{
		_storage?.Dispose();
	}

	[Test]
	public async Task Should_allow_append_and_read_items()
	{
		await _storage.AppendAsync(new TestItem { Id = 1, Name = "Test Item 1" });
		await _storage.AppendAsync(new TestItem { Id = 2, Name = "Test Item 2" });

		await ReopenAsync();

		var items = _storage.GetAll<TestItem>().ToBlockingEnumerable().ToList();

		await Assert.That(items).HasCount(2);

		await Assert.That(items[0].Id).IsEqualTo(1);
		await Assert.That(items[0].Name).IsEqualTo("Test Item 1");
		await Assert.That(items[1].Id).IsEqualTo(2);
		await Assert.That(items[1].Name).IsEqualTo("Test Item 2");
	}
}

[InheritsTests]
public class JsonlStorageTests : StorageTests
{
	string _fileName;

	public JsonlStorageTests()
	{
		HostBuilder.AddJsonLinesStorage();
		var options = TestJsonSerializerContext.Default.Options;
		ServiceCollection.AddSingleton(options);
	}

	protected override async Task ReopenAsync()
	{
		_storage.Dispose();
		var fix = new JsonlStorageTests();
		fix.Configuration["JsonLinesStorage:FileName"] = _fileName;
		_storage = fix.ServiceProvider.GetRequiredService<IStorage>();
	}

	[Before(Test)]
	public void Before()
	{
		Configuration["JsonLinesStorage:FileName"] = _fileName = CreateTestFileName("data.jsonl");
		_storage = ServiceProvider.GetRequiredService<IStorage>();
	}

	[Test]
	public async Task Should_store_objects_as_jsonl()
	{
		await _storage.AppendAsync(new TestItem { Id = 1, Name = "Test Item 1" });
		await _storage.AppendAsync(new TestItem { Id = 2, Name = "Test Item 2" });

		_storage.Dispose();
		await Assert.That(FileReadAllText(_fileName).Replace("\r\n", "\n")).IsEqualTo("""
{"Synqra.Storage.Jsonl":"0.1","rootItemType":"Synqra.Tests.TestItem"}
{"id":1,"name":"Test Item 1"}
{"id":2,"name":"Test Item 2"}

""".Replace("\r\n", "\n"));
	}

	[Test]
	public async Task Should_store_polimorfic_as_jsonl()
	{
		await _storage.AppendAsync<Event>(new ObjectCreatedEvent
		{
			CollectionId = default,
			CommandId = default,
			EventId = default,
			TargetId = default,
			TargetTypeId = default,
		});

		_storage.Dispose();
		await Assert.That(FileReadAllText(_fileName).Replace("\r\n", "\n")).IsEqualTo("""
{"Synqra.Storage.Jsonl":"0.1","rootItemType":"Synqra.Event"}
{"_t":"ObjectCreatedEvent","targetId":"00000000-0000-0000-0000-000000000000","targetTypeId":"00000000-0000-0000-0000-000000000000","collectionId":"00000000-0000-0000-0000-000000000000","eventId":"00000000-0000-0000-0000-000000000000","commandId":"00000000-0000-0000-0000-000000000000"}

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
		var item = new TestItem { Id = 1, Name = "Test Item 1" };
		MeasurePerformance(async () => {
			await _storage.AppendAsync(item);
		});
	}

	string FileReadAllText(string fileName)
	{
		using var sr = new StreamReader(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite /* Main Difference */), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 64);
		return sr.ReadToEnd();
	}
}
