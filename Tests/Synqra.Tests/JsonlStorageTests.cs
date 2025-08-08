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
[NotInParallel]
public class JsonlStorageTests : StorageTests
{
	string _fileName;

	public JsonlStorageTests()
	{
		HostBuilder.AddJsonLinesStorage();
		ServiceCollection.AddSingleton(TestJsonSerializerContext.Default.Options);
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
		Configuration["JsonLinesStorage:FileName"] = _fileName = $"TestData/data_{Guid.NewGuid():N}.jsonl";
		Directory.CreateDirectory(Path.GetDirectoryName(_fileName));
		// Ensure the file is deleted before each test
		if (File.Exists(_fileName))
		{
			File.Delete(_fileName);
		}
		_storage = ServiceProvider.GetRequiredService<IStorage>();
	}

	[Test]
	public async Task Should_store_objects_as_jsonl()
	{
		await _storage.AppendAsync(new TestItem { Id = 1, Name = "Test Item 1" });
		await _storage.AppendAsync(new TestItem { Id = 2, Name = "Test Item 2" });

		_storage.Dispose();
		await Assert.That(FileReadAllText(_fileName)).IsEqualTo("""
{"Synqra.Storage.Jsonl":"1.0.0","itemType":"Synqra.Tests.TestItem"}
{"id":1,"name":"Test Item 1"}
{"id":2,"name":"Test Item 2"}

""");
	}

	[Test]
	[Explicit]
	public async Task Should_write_quickly()
	{
		int id = 0;
		var perf = PerformanceTestUtils.MeasurePerformance(async () => {
			await _storage.AppendAsync(new TestItem { Id = ++id, Name = "Test Item " + id });
		});

		Console.WriteLine(perf.DeviationFactor);
		Console.WriteLine(perf.OperationsPerSecond);
	}

	[Test]
	[Explicit]
	public async Task Should_write_quickly2()
	{
		var item = new TestItem { Id = 1, Name = "Test Item 1" };
		var perf = PerformanceTestUtils.MeasurePerformance(async () => {
			await _storage.AppendAsync(item);
		});

		Console.WriteLine(perf.DeviationFactor);
		Console.WriteLine(perf.OperationsPerSecond);
	}

	string FileReadAllText(string fileName)
	{
		using var sr = new StreamReader(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite /* Main Difference */), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 64);
		return sr.ReadToEnd();
	}
}
