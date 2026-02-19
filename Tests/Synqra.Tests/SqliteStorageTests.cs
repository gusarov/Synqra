using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage;
using Synqra.AppendStorage.Sqlite;
using Synqra.BinarySerializer;
using Synqra.Tests.TestHelpers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Synqra.Tests;

/// <summary>
/// Minimal serializer for testing SQLite storage in isolation.
/// Hand-rolls Guid+string so we avoid any reflection / source-gen dependency.
/// </summary>
file class TestBinarySerializer : ISBXSerializer
{
	public void Serialize<T>(in Span<byte> buffer, T value, ref int pos)
	{
		if (value is SqliteTestItem item)
		{
			item.Id.TryWriteBytes(buffer.Slice(pos));
			pos += 16;
			var nameBytes = Encoding.UTF8.GetBytes(item.Name);
			BitConverter.TryWriteBytes(buffer.Slice(pos), nameBytes.Length);
			pos += 4;
			nameBytes.CopyTo(buffer.Slice(pos));
			pos += nameBytes.Length;
			return;
		}
		throw new NotSupportedException($"Cannot serialize {typeof(T)}");
	}

	public T Deserialize<T>(in ReadOnlySpan<byte> buffer, ref int pos)
	{
		if (typeof(T) == typeof(SqliteTestItem))
		{
			var id = new Guid(buffer.Slice(pos, 16));
			pos += 16;
			var nameLen = BitConverter.ToInt32(buffer.Slice(pos, 4));
			pos += 4;
			var name = Encoding.UTF8.GetString(buffer.Slice(pos, nameLen));
			pos += nameLen;
			return (T)(object)new SqliteTestItem { Id = id, Name = name };
		}
		throw new NotSupportedException($"Cannot deserialize {typeof(T)}");
	}

	public void Snapshot() { }
	public void Reset() { }
	public void Serialize(in Span<byte> buffer, string value, ref int pos) => throw new NotImplementedException();
	public void Serialize(in Span<byte> buffer, in long value, ref int pos) => throw new NotImplementedException();
	public void Serialize(in Span<byte> buffer, ulong value, ref int pos) => throw new NotImplementedException();
	public string? DeserializeString(in ReadOnlySpan<byte> buffer, ref int pos) => throw new NotImplementedException();
	public long DeserializeSigned(in ReadOnlySpan<byte> buffer, ref int pos) => throw new NotImplementedException();
	public ulong DeserializeUnsigned(in ReadOnlySpan<byte> buffer, ref int pos) => throw new NotImplementedException();
	public IList<T> DeserializeList<T>(in ReadOnlySpan<byte> buffer, ref int pos) => throw new NotImplementedException();
	public IDictionary<TK, TV> DeserializeDict<TK, TV>(in ReadOnlySpan<byte> buffer, ref int pos) => throw new NotImplementedException();
}

file class TestSerializerFactory : ISBXSerializerFactory
{
	public ISBXSerializer CreateSerializer() => new TestBinarySerializer();
}

class SqliteTestItem
{
	public Guid Id { get; set; }
	public string Name { get; set; } = "";
}

[NotInParallel]
public class SqliteStorageTests : BaseTest
{
	private IAppendStorage<SqliteTestItem, Guid>? _storage;
	private string _dbPath = null!;

	[Before(Test)]
	public void Setup()
	{
		_dbPath = CreateTestFileName("sqlite_test.db");
		_storage = CreateStorage(_dbPath);
	}

	[After(Test)]
	public void Cleanup()
	{
		_storage?.Dispose();
	}

	private static SqliteAppendStorage<SqliteTestItem, Guid> CreateStorage(string dbPath)
	{
		var options = Options.Create(new SqliteAppendStorageOptions
		{
			ConnectionString = $"Data Source={dbPath}",
		});
		return new SqliteAppendStorage<SqliteTestItem, Guid>(
			options,
			new TestSerializerFactory(),
			item => item.Id);
	}

	[Test]
	public async Task Should_append_and_read_single_item()
	{
		var id = GuidExtensions.CreateVersion7();
		await _storage!.AppendAsync(new SqliteTestItem { Id = id, Name = "Item1" });

		var items = _storage.GetAllAsync().ToBlockingEnumerable().ToList();

		await Assert.That(items).HasCount(1);
		await Assert.That(items[0].Id).IsEqualTo(id);
		await Assert.That(items[0].Name).IsEqualTo("Item1");
	}

	[Test]
	public async Task Should_append_and_read_multiple_items()
	{
		var id1 = GuidExtensions.CreateVersion7();
		await Task.Delay(1); // ensure distinct v7 timestamps
		var id2 = GuidExtensions.CreateVersion7();

		await _storage!.AppendAsync(new SqliteTestItem { Id = id1, Name = "First" });
		await _storage.AppendAsync(new SqliteTestItem { Id = id2, Name = "Second" });

		var items = _storage.GetAllAsync().ToBlockingEnumerable().ToList();

		await Assert.That(items).HasCount(2);
		await Assert.That(items[0].Name).IsEqualTo("First");
		await Assert.That(items[1].Name).IsEqualTo("Second");
	}

	[Test]
	public async Task Should_batch_append_items()
	{
		var id1 = GuidExtensions.CreateVersion7();
		await Task.Delay(1);
		var id2 = GuidExtensions.CreateVersion7();
		await Task.Delay(1);
		var id3 = GuidExtensions.CreateVersion7();

		await _storage!.AppendBatchAsync([
			new SqliteTestItem { Id = id1, Name = "A" },
			new SqliteTestItem { Id = id2, Name = "B" },
			new SqliteTestItem { Id = id3, Name = "C" },
		]);

		var items = _storage.GetAllAsync().ToBlockingEnumerable().ToList();

		await Assert.That(items).HasCount(3);
		await Assert.That(items[0].Name).IsEqualTo("A");
		await Assert.That(items[1].Name).IsEqualTo("B");
		await Assert.That(items[2].Name).IsEqualTo("C");
	}

	[Test]
	public async Task Should_batch_append_each_item_with_distinct_key()
	{
		var id1 = GuidExtensions.CreateVersion7();
		await Task.Delay(1);
		var id2 = GuidExtensions.CreateVersion7();

		// This must not throw — each item has its own unique key
		await _storage!.AppendBatchAsync([
			new SqliteTestItem { Id = id1, Name = "X" },
			new SqliteTestItem { Id = id2, Name = "Y" },
		]);

		var items = _storage.GetAllAsync().ToBlockingEnumerable().ToList();

		await Assert.That(items).HasCount(2);
		await Assert.That(items[0].Id).IsEqualTo(id1);
		await Assert.That(items[1].Id).IsEqualTo(id2);
	}

	[Test]
	public async Task Should_preserve_chronological_order_for_v7_guids()
	{
		var ids = new List<Guid>();
		for (int i = 0; i < 5; i++)
		{
			ids.Add(GuidExtensions.CreateVersion7());
			await Task.Delay(1);
		}

		// Insert in reverse order
		for (int i = ids.Count - 1; i >= 0; i--)
		{
			await _storage!.AppendAsync(new SqliteTestItem { Id = ids[i], Name = $"Item{i}" });
		}

		// Should come back sorted by GUID (chronological for v7)
		var items = _storage!.GetAllAsync().ToBlockingEnumerable().ToList();

		await Assert.That(items).HasCount(5);
		for (int i = 0; i < 5; i++)
		{
			await Assert.That(items[i].Id).IsEqualTo(ids[i]);
		}
	}

	[Test]
	public async Task Should_filter_by_from_key()
	{
		var id1 = GuidExtensions.CreateVersion7();
		await Task.Delay(1);
		var id2 = GuidExtensions.CreateVersion7();
		await Task.Delay(1);
		var id3 = GuidExtensions.CreateVersion7();

		await _storage!.AppendAsync(new SqliteTestItem { Id = id1, Name = "A" });
		await _storage.AppendAsync(new SqliteTestItem { Id = id2, Name = "B" });
		await _storage.AppendAsync(new SqliteTestItem { Id = id3, Name = "C" });

		var items = _storage.GetAllAsync(from: id2).ToBlockingEnumerable().ToList();

		await Assert.That(items).HasCount(2);
		await Assert.That(items[0].Name).IsEqualTo("B");
		await Assert.That(items[1].Name).IsEqualTo("C");
	}

	[Test]
	public async Task Should_survive_reopen()
	{
		var id = GuidExtensions.CreateVersion7();
		await _storage!.AppendAsync(new SqliteTestItem { Id = id, Name = "Persisted" });
		_storage.Dispose();

		using var reopened = CreateStorage(_dbPath);
		var items = reopened.GetAllAsync().ToBlockingEnumerable().ToList();

		await Assert.That(items).HasCount(1);
		await Assert.That(items[0].Id).IsEqualTo(id);
		await Assert.That(items[0].Name).IsEqualTo("Persisted");
	}

	[Test]
	public async Task Should_reject_duplicate_key()
	{
		var id = GuidExtensions.CreateVersion7();
		await _storage!.AppendAsync(new SqliteTestItem { Id = id, Name = "First" });

		var ex = await Assert.ThrowsAsync(() =>
			_storage.AppendAsync(new SqliteTestItem { Id = id, Name = "Duplicate" }));
	}

	[Test]
	public async Task Should_test_roundtrip()
	{
		var result = await _storage!.TestAsync("ping");
		await Assert.That(result).IsEqualTo("ping");
	}
}
