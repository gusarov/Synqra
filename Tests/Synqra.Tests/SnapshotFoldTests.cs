using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.BinarySerializer;
using Synqra.Projection.InMemory;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>The snapshot writer's chunks: every record lands in exactly one chunk, and the chunks are numbered without a gap.</summary>
public class SnapshotChunkWriterTests
{
	/// <summary>
	/// A record that does not fit in the open chunk closes it, and when that record is past the budget on its own its chunk is
	/// full at once. Closing both in one call returned only the second: the first was never sent, and a client waiting for its
	/// index reconnected and asked for the same snapshot forever.
	/// </summary>
	[Test]
	public async Task Should_hand_out_every_chunk_when_an_oversized_record_follows_smaller_ones()
	{
		var serializer = new SbxSerializer();
		serializer.Snapshot();
		var writer = new SnapshotChunkWriter(serializer, Guid.CreateVersion7(), budget: 256);
		var records = new List<SnapshotRecord>
		{
			new() { ComponentId = Guid.CreateVersion7(), Data = "small 1" },
			new() { ComponentId = Guid.CreateVersion7(), Data = "small 2" },
			new() { ComponentId = Guid.CreateVersion7(), Data = new string('x', 20_000) }, // past the budget and the open chunk's buffer
			new() { ComponentId = Guid.CreateVersion7(), Data = "small 3" },
		};

		var chunks = new List<SnapshotChunk>();
		foreach (var record in records)
		{
			if (writer.Write(record) is { } chunk)
			{
				chunks.Add(chunk);
			}
		}
		if (writer.Complete() is { } last)
		{
			chunks.Add(last);
		}

		await Assert.That(chunks.Select(x => x.Index)).IsEquivalentTo(Enumerable.Range(0, chunks.Count).Select(x => (long)x));
		await Assert.That(writer.ChunkCount).IsEqualTo(chunks.Count);
		var reader = new SnapshotChunkReader(serializer);
		var read = chunks.SelectMany(reader.Read).ToList();
		await Assert.That(read.Select(x => x.ComponentId)).IsEquivalentTo(records.Select(x => x.ComponentId));
	}
}

/// <summary>A client's projection folding a stream snapshot: a record it cannot take costs that record only.</summary>
public class SnapshotFoldTests : TestsStateManagement
{
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.Services.AddInMemorySynqraStore();
		hostApplicationBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(250, typeof(TestComponentNode));
			ser.Map(251, typeof(TestUniqueComponent));
			ser.Map(252, typeof(TestTaggingComponent));
		});
	}

	/// <summary>
	/// A second unique facet on one entity is vetoed. The fold stepped over the whole snapshot at the first such record, and the
	/// client, whose watermark is already past the snapshot, was never sent the rest of the state again.
	/// </summary>
	[Test]
	public async Task Should_step_over_a_record_it_cannot_take_and_apply_the_rest()
	{
		var stream = Guid.NewGuid();
		var a = Guid.CreateVersion7();
		var b = Guid.CreateVersion7();
		var versionA = Guid.CreateVersion7();
		var snapshot = ComposeSnapshot(stream,
		[
			new() { ComponentId = a, Version = versionA, Data = new TestComponentNode { Name = "a" } },
			new() { ComponentId = b, Data = new TestComponentNode { Name = "b" } },
			new() { ComponentId = Guid.CreateVersion7(), Owner = a, Data = new TestUniqueComponent { Subject = "first" } },
			new() { ComponentId = Guid.CreateVersion7(), Owner = a, Data = new TestUniqueComponent { Subject = "vetoed" } },
			new() { ComponentId = Guid.CreateVersion7(), Owner = a, Data = new TestTaggingComponent { Tag = "after the veto" } },
			new() { ComponentId = Guid.CreateVersion7(), Owner = b, Data = new TestUniqueComponent { Subject = "b's own" } },
		]);
		var projection = (InMemoryProjection)ServiceProvider.GetRequiredService<IProjectionFactory>().Create(stream);

		await projection.ApplyAsync(snapshot, isReplay: true);

		var nodes = projection.GetCollection<TestComponentNode>().ToDictionary(x => x.Name!);
		await Assert.That(nodes.Keys).IsEquivalentTo(["a", "b"]);
		await Assert.That(nodes["a"].Components.OfType<TestUniqueComponent>().Select(x => x.Subject)).IsEquivalentTo(["first"]);
		await Assert.That(nodes["a"].Components.OfType<TestTaggingComponent>().Select(x => x.Tag)).IsEquivalentTo(["after the veto"]);
		await Assert.That(nodes["b"].Components.OfType<TestUniqueComponent>().Select(x => x.Subject)).IsEquivalentTo(["b's own"]);
		await Assert.That(projection.GetLastEventId(a)).IsEqualTo(versionA).Because("an entity keeps the version it had on the master");
	}

	StreamSnapshotEvent ComposeSnapshot(Guid stream, List<SnapshotRecord> records)
	{
		var schema = new SnapshotSchema(ServiceProvider.GetRequiredService<ISbxSerializerFactory>());
		var serializer = schema.CreateWriter();
		var id = Guid.CreateVersion7();
		var writer = new SnapshotChunkWriter(serializer, id);
		var chunks = new List<SnapshotChunk>();
		foreach (var record in records)
		{
			if (writer.Write(record) is { } chunk)
			{
				chunks.Add(chunk);
			}
		}
		if (writer.Complete() is { } last)
		{
			chunks.Add(last);
		}
		return new StreamSnapshotEvent
		{
			EventId = id,
			CommandId = id,
			StreamId = stream,
			Header = schema.DescribeSnapshot(serializer, stream, id, Guid.CreateVersion7(), records.Count),
			Chunks = chunks,
		};
	}
}
