using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage.BlobStorage;
using Synqra.BinarySerializer;
using Synqra.BlobStorage;
using Synqra.Projection.InMemory;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// A browser reads its whole event log on every page load (the replication service's dedup, the projection's cold fold).
/// One IndexedDB round trip per event made that minutes on a large stream, and every click waited for it (prod 2026-10-22).
/// A store that reads pages serves the log a page per round trip.
/// </summary>
public class BlobLogPageReadTests : TestsStateManagement
{
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.Services.AddInMemorySynqraStore();
		hostApplicationBuilder.Services.AddSbxSerializer(ser => ser.Map(240, typeof(TestComponentNode)));
	}

	[Test]
	public async Task Should_read_a_log_a_page_at_a_time_when_the_store_reads_pages()
	{
		var store = new PagingStore(pageSize: 4);
		var serializers = ServiceProvider.GetRequiredService<ISbxSerializerFactory>();
		var writer = new ClientEventLog(new BlobEventStorage(store, serializers), new EventSequencer(Options.Create(new EventSequencerOptions())));
		var streamId = Guid.NewGuid();
		var written = new List<Guid>();
		for (var i = 0; i < 10; i++)
		{
			var ev = NodeAdded(streamId);
			await writer.AppendAsync(ev);
			written.Add(ev.EventId);
		}

		var nextLoad = new BlobEventStorage(store, serializers); // a page load: nothing attached in memory yet
		var read = new List<Guid>();
		await foreach (var ev in nextLoad.GetAllAsync())
		{
			read.Add(ev.EventId);
		}

		await Assert.That(read).IsEquivalentTo(written, TUnit.Assertions.Enums.CollectionOrdering.Matching);
		await Assert.That(store.SingleReads).IsEqualTo(0).Because("no record is read on its own");
		await Assert.That(store.PageReads).IsEqualTo(3).Because("ten records in pages of four");
	}

	ComponentAddedEvent NodeAdded(Guid streamId)
	{
		var container = ServiceProvider.GetRequiredService<ITypeMetadataProvider>().GetTypeMetadata(typeof(TestComponentNode));
		var nodeId = Guid.NewGuid();
		return new ComponentAddedEvent
		{
			EventId = Guid.CreateVersion7(),
			CommandId = Guid.NewGuid(),
			StreamId = streamId,
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = nodeId,
			ComponentTypeId = container.TypeId,
			ComponentId = nodeId,
			Data = new TestComponentNode { Name = "paged" },
		};
	}

	/// <summary>A store in memory that reads pages and counts both kinds of read.</summary>
	sealed class PagingStore(int pageSize) : IBlobStorage<(Guid Stream, Guid Sequence)>, IBlobPageReader<(Guid Stream, Guid Sequence)>
	{
		readonly SortedDictionary<(Guid Stream, Guid Sequence), byte[]> _blobs = new(Comparer<(Guid Stream, Guid Sequence)>.Create((a, b) => a.Stream.CompareBigEndian(b.Stream) is var first && first != 0 ? first : a.Sequence.CompareBigEndian(b.Sequence)));

		public int SingleReads { get; private set; }

		public int PageReads { get; private set; }

		public async IAsyncEnumerable<KeyValuePair<(Guid Stream, Guid Sequence), byte[]>> EnumerateBlobsAsync((Guid Stream, Guid Sequence) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			var after = _blobs.Where(e => from == default || _blobs.Comparer.Compare(e.Key, from) > 0).ToList();
			for (var start = 0; start < after.Count; start += pageSize)
			{
				PageReads++;
				await Task.Yield();
				foreach (var entry in after.Skip(start).Take(pageSize))
				{
					yield return entry;
				}
			}
		}

		public async IAsyncEnumerable<(Guid Stream, Guid Sequence)> EnumerateKeysAsync((Guid Stream, Guid Sequence) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			foreach (var key in _blobs.Keys.Where(k => from == default || _blobs.Comparer.Compare(k, from) > 0).ToList())
			{
				await Task.Yield();
				yield return key;
			}
		}

		public ValueTask<byte[]> ReadBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default)
		{
			SingleReads++;
			return ValueTask.FromResult(_blobs[key]);
		}

		public ValueTask WriteBlobAsync((Guid Stream, Guid Sequence) key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
		{
			_blobs.Add(key, blob.ToArray());
			return ValueTask.CompletedTask;
		}

		public ValueTask DeleteBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default)
		{
			_blobs.Remove(key);
			return ValueTask.CompletedTask;
		}

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
