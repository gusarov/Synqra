using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synqra.AppendStorage;
using Synqra.AppendStorage.BlobStorage;
using Synqra.BinarySerializer;
using Synqra.BlobStorage;
using Synqra.Projection.InMemory;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// Browser tabs share one IndexedDB event log and do not talk to each other. Every tab's replication receives every pushed
/// event and appends it; every tab stamps with its own clock. The store decides instead: an append lands only at its tail
/// and once per EventId, in one transaction (here a lock, as IndexedDB serializes readwrite transactions across tabs).
/// </summary>
public class SharedBrowserLogTests : TestsStateManagement
{
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.Services.AddInMemorySynqraStore();
		hostApplicationBuilder.Services.AddSbxSerializer(ser => ser.Map(240, typeof(TestComponentNode))); // the events here carry it through SBX
		hostApplicationBuilder.Services.AddSingleton<SharedTailStore>();
		hostApplicationBuilder.Services.AddSingleton<IBlobStorage<(Guid Stream, Guid Sequence)>>(sp => sp.GetRequiredService<SharedTailStore>());
		hostApplicationBuilder.Services.Replace(ServiceDescriptor.Singleton<IAppendStorage<Event, (Guid Stream, Guid Sequence)>, BlobEventStorage>()); // the base host registers its own
		hostApplicationBuilder.Services.AddClientEventLog();
	}

	SharedTailStore Store => ServiceProvider.GetRequiredService<SharedTailStore>();

	/// <summary>This host is one tab; the second tab is its own event storage and log over the same store.</summary>
	IEventLog SecondTab()
	{
		var storage = new BlobEventStorage(Store, ServiceProvider.GetRequiredService<ISbxSerializerFactory>());
		return new ClientEventLog(storage, new EventSequencer(Options.Create(new EventSequencerOptions())));
	}

	[Test]
	public async Task Should_keep_one_copy_of_an_event_both_tabs_received()
	{
		var streamId = Guid.NewGuid();
		var eventId = Guid.CreateVersion7();
		var tabA = ServiceProvider.GetRequiredService<IEventLog>();
		var tabB = SecondTab();

		await tabA.AppendAsync(NodeAdded(streamId, eventId, Guid.NewGuid())); // each tab's replication deserialized its own copy
		await tabB.AppendAsync(NodeAdded(streamId, eventId, Guid.NewGuid()));

		await Assert.That(Store.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_order_two_tabs_appends_by_the_store_not_by_a_tabs_clock()
	{
		var streamId = Guid.NewGuid();
		var tabA = ServiceProvider.GetRequiredService<IEventLog>();
		var tabB = SecondTab();
		var fromB = NodeAdded(streamId, Guid.CreateVersion7(), Guid.NewGuid());
		var fromA = NodeAdded(streamId, Guid.CreateVersion7(), Guid.NewGuid());
		Store.BeforeNextAppend = () => tabB.AppendAsync(fromB); // tab B lands between tab A's tail read and its append

		await tabA.AppendAsync(fromA);

		await Assert.That(Store.TailMoves).IsEqualTo(1);
		await Assert.That(fromB.SequenceId.CompareBigEndian(fromA.SequenceId)).IsLessThan(0); // tab A stamped again, after B
		var order = new List<Guid>();
		await foreach (var ev in tabA.ReadStreamAsync(streamId))
		{
			order.Add(ev.EventId);
		}
		await Assert.That(order).IsEquivalentTo([fromB.EventId, fromA.EventId]);
	}

	/// <summary>
	/// A projection used to move its cursor to its own write's stamp. An event another tab appended just before that write,
	/// not yet folded in here, then sat behind the cursor: skipped until a reload. Now the write catches up instead.
	/// </summary>
	[Test]
	public async Task Should_apply_another_tabs_event_that_landed_before_a_local_write()
	{
		var provider = ServiceProvider.GetRequiredService<IProjectionProvider>();
		var keeper = ServiceProvider.GetRequiredService<IProjectionKeeper>();
		var streamId = Guid.NewGuid();
		var projection = await provider.GetAsync(streamId);
		var store = (IObjectStore)projection;
		var container = store.TypeMetadataProvider.GetTypeMetadata(typeof(TestComponentNode));

		var foreignId = Guid.NewGuid();
		await SecondTab().AppendAsync(NodeAdded(streamId, Guid.CreateVersion7(), foreignId, container)); // not folded in here yet
		var localId = Guid.NewGuid();
		await store.SubmitCommandAsync(new AddComponentCommand
		{
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = localId,
			ComponentTypeId = container.TypeId,
			ComponentId = localId,
			Data = new TestComponentNode { Name = "local" },
		});
		await keeper.MaintainAsync(projection);

		var nodes = store.GetCollection<TestComponentNode>().Select(n => store.GetId(n)).ToList();
		await Assert.That(nodes).Contains(foreignId);
		await Assert.That(nodes.Count(id => id == localId)).IsEqualTo(1); // its own write is not applied again from the log
	}

	ComponentAddedEvent NodeAdded(Guid streamId, Guid eventId, Guid nodeId, TypeMetadata? container = null)
	{
		container ??= ServiceProvider.GetRequiredService<ITypeMetadataProvider>().GetTypeMetadata(typeof(TestComponentNode));
		return new ComponentAddedEvent
		{
			EventId = eventId,
			CommandId = Guid.NewGuid(),
			StreamId = streamId,
			TargetTypeId = container.TypeId,
			CollectionId = container.CollectionId,
			TargetId = nodeId,
			ComponentTypeId = container.TypeId,
			ComponentId = nodeId,
			Data = new TestComponentNode { Name = "pushed" },
		};
	}

	/// <summary>The IndexedDB contract in memory: one lock stands for IndexedDB's one-readwrite-transaction-at-a-time, across tabs.</summary>
	public sealed class SharedTailStore : ITailAppendBlobStorage<(Guid Stream, Guid Sequence)>, IClearableBlobStorage, ITaggedBlobStorage<(Guid Stream, Guid Sequence)>
	{
		readonly SortedDictionary<(Guid Stream, Guid Sequence), byte[]> _blobs = new(Comparer<(Guid Stream, Guid Sequence)>.Create((a, b) => a.Stream.CompareBigEndian(b.Stream) is var first && first != 0 ? first : a.Sequence.CompareBigEndian(b.Sequence)));
		readonly Dictionary<(Guid Stream, Guid Sequence), uint> _recordTags = new(); // a record's `tag` field, as IndexedDB keeps it
		readonly Dictionary<string, byte[]> _sides = new(); // "{store}#{side}", here "{side}/{key}"
		readonly HashSet<string> _tags = [];

		/// <summary>Runs once, inside the next append before its checks: another tab's write landing in between.</summary>
		public Func<Task>? BeforeNextAppend { get; set; }

		public int TailMoves { get; private set; }

		public int Count
		{
			get
			{
				lock (_blobs)
				{
					return _blobs.Count;
				}
			}
		}

		public ValueTask<(Guid Stream, Guid Sequence)> ReadTailAsync(CancellationToken cancellationToken = default)
		{
			lock (_blobs)
			{
				return ValueTask.FromResult(_blobs.Count == 0 ? default : _blobs.Keys.Last());
			}
		}

		public async ValueTask<TailAppendResult> AppendAtTailAsync((Guid Stream, Guid Sequence) key, ReadOnlyMemory<byte> blob, string? json, string uniqueTag, (Guid Stream, Guid Sequence) expectedTail, uint? tag = null, CancellationToken cancellationToken = default)
		{
			var interleave = BeforeNextAppend;
			BeforeNextAppend = null;
			if (interleave is not null)
			{
				await interleave();
			}
			TailAppendResult outcome;
			lock (_blobs)
			{
				var tail = _blobs.Count == 0 ? default : _blobs.Keys.Last();
				if (_tags.Contains(uniqueTag))
				{
					outcome = TailAppendResult.Duplicate;
				}
				else if (tail != expectedTail)
				{
					TailMoves++;
					outcome = TailAppendResult.TailMoved;
				}
				else
				{
					_blobs.Add(key, blob.ToArray());
					if (tag is { } recordTag)
					{
						_recordTags[key] = recordTag;
					}
					_tags.Add(uniqueTag);
					outcome = TailAppendResult.Appended;
				}
			}
			return outcome;
		}

		public async IAsyncEnumerable<(Guid Stream, Guid Sequence)> EnumerateKeysAsync((Guid Stream, Guid Sequence) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			List<(Guid Stream, Guid Sequence)> keys;
			lock (_blobs)
			{
				keys = [.. _blobs.Keys.Where(k => from == default || _blobs.Comparer.Compare(k, from) > 0)];
			}
			foreach (var key in keys)
			{
				await Task.Yield();
				yield return key;
			}
		}

		public ValueTask<byte[]> ReadBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default)
		{
			lock (_blobs)
			{
				return ValueTask.FromResult(_blobs[key]);
			}
		}

		public ValueTask WriteBlobAsync((Guid Stream, Guid Sequence) key, ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default)
		{
			lock (_blobs)
			{
				_blobs.Add(key, blob.ToArray());
			}
			return ValueTask.CompletedTask;
		}

		public ValueTask DeleteBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default)
		{
			lock (_blobs)
			{
				_blobs.Remove(key);
			}
			return ValueTask.CompletedTask;
		}

		/// <summary>Wipes the log, as a browser's IndexedDB one can be.</summary>
		public Task ClearAllAsync(CancellationToken cancellationToken = default)
		{
			lock (_blobs)
			{
				_blobs.Clear();
				_recordTags.Clear();
				_sides.Clear();
				_tags.Clear();
			}
			return Task.CompletedTask;
		}

		public ValueTask WriteBlobAsync((Guid Stream, Guid Sequence) key, ReadOnlyMemory<byte> blob, uint tag, string? json, CancellationToken cancellationToken = default)
		{
			lock (_blobs)
			{
				_blobs.Add(key, blob.ToArray());
				_recordTags[key] = tag;
			}
			return ValueTask.CompletedTask;
		}

		public ValueTask<TaggedBlob> ReadTaggedBlobAsync((Guid Stream, Guid Sequence) key, CancellationToken cancellationToken = default)
		{
			lock (_blobs)
			{
				return ValueTask.FromResult(new TaggedBlob(_blobs[key], _recordTags.TryGetValue(key, out var bundle) ? bundle : null));
			}
		}

		public async IAsyncEnumerable<((Guid Stream, Guid Sequence) Key, TaggedBlob Blob)> EnumerateTaggedBlobsAsync((Guid Stream, Guid Sequence) from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await foreach (var key in EnumerateKeysAsync(from, cancellationToken))
			{
				yield return (key, await ReadTaggedBlobAsync(key, cancellationToken));
			}
		}

		public ValueTask<IReadOnlyDictionary<string, byte[]>> ReadSideEntriesAsync(string side, CancellationToken cancellationToken = default)
		{
			lock (_blobs)
			{
				return ValueTask.FromResult<IReadOnlyDictionary<string, byte[]>>(_sides.Where(x => x.Key.StartsWith(side + "/", StringComparison.Ordinal)).ToDictionary(x => x.Key[(side.Length + 1)..], x => x.Value));
			}
		}

		public ValueTask StoreSideEntryAsync(string side, string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
		{
			lock (_blobs)
			{
				_sides[side + "/" + key] = content.ToArray(); // IndexedDB put: the same entry again changes nothing
			}
			return ValueTask.CompletedTask;
		}

		/// <summary>Overwrites a side entry, as a damaged or colliding one would read.</summary>
		public void TamperSideEntry(string side, string key, byte[] content)
		{
			lock (_blobs)
			{
				_sides[side + "/" + key] = content;
			}
		}

		/// <summary>The keys of the side <paramref name="side"/>.</summary>
		public IReadOnlyCollection<string> ListSideKeys(string side)
		{
			lock (_blobs)
			{
				return [.. _sides.Keys.Where(x => x.StartsWith(side + "/", StringComparison.Ordinal)).Select(x => x[(side.Length + 1)..])];
			}
		}

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
