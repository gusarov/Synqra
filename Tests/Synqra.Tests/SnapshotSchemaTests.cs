using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Synqra.BinarySerializer;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// The snapshot header pins every type the payload uses, so a reader decodes exactly the layout the writer wrote and refuses
/// a snapshot it cannot read exactly (plans/snapshot.md §2).
/// </summary>
public class SnapshotSchemaTests
{
	readonly Guid _stream = Guid.NewGuid();
	readonly Guid _snapshot = GuidExtensions.CreateVersion7();
	readonly Guid _watermark = GuidExtensions.CreateVersion7();

	[Test]
	public async Task Should_pin_every_mapped_type_at_the_version_it_is_written()
	{
		var schema = CreateSchema();
		var writer = schema.CreateWriter();

		var header = schema.DescribeSnapshot(writer, _stream, _snapshot, _watermark, entityCount: 0);
		var added = header.Types.Single(x => x.TypeId == new Guid("C0DEADD0-1032-8000-8E01-000000000000"));

		await Assert.That(header.Types.Count).IsEqualTo(writer.Mappings.Count);
		await Assert.That(header.FormatVersion).IsEqualTo(schema.FormatVersion);
		await Assert.That(added.MapId).IsEqualTo(-66);
		await Assert.That((float)added.Version).IsEqualTo(2026.789f);
		await Assert.That(added.Schema).IsEqualTo("1 EventId Guid CommandId Guid SequenceId Guid TargetId Guid TargetTypeId Guid CollectionId Guid ComponentTypeId Guid ComponentId Guid Data object?");
	}

	[Test]
	public async Task Should_read_records_written_with_the_writer_mapping()
	{
		var schema = CreateSchema();
		var writer = schema.CreateWriter();
		var header = schema.DescribeSnapshot(writer, _stream, _snapshot, _watermark, entityCount: 1);
		var component = Guid.NewGuid();
		var version = GuidExtensions.CreateVersion7();
		var data = new EventRejected { EventId = Guid.NewGuid(), Reason = "a mapped model as record data" };
		var buffer = new byte[1024];
		var written = 0;
		writer.Serialize(buffer, ref written, component);
		writer.Serialize(buffer, ref written, (Guid?)null);
		writer.Serialize(buffer, ref written, (Guid?)version);
		writer.Serialize<object>(buffer, ref written, data);

		var reader = schema.CreateReader(header);
		var read = 0;
		var readComponent = reader.DeserializeGuid(buffer, ref read);
		var readOwner = reader.DeserializeNullableGuid(buffer, ref read);
		var readVersion = reader.DeserializeNullableGuid(buffer, ref read);
		var readData = (EventRejected)reader.Deserialize<object>(buffer, ref read);

		await Assert.That(readComponent).IsEqualTo(component);
		await Assert.That(readOwner).IsNull();
		await Assert.That(readVersion).IsEqualTo(version);
		await Assert.That(readData.EventId).IsEqualTo(data.EventId);
		await Assert.That(readData.Reason).IsEqualTo(data.Reason);
		await Assert.That(read).IsEqualTo(written);
	}

	[Test]
	public async Task Should_read_an_older_schema_the_type_still_lists()
	{
		var schema = CreateSchema();
		var writer = schema.CreateWriter();
		writer.Map(-66, 2026.405, typeof(ComponentAddedEvent)); // a writer one schema behind: no SequenceId
		var header = schema.DescribeSnapshot(writer, _stream, _snapshot, _watermark, entityCount: 1);
		var added = new ComponentAddedEvent
		{
			EventId = Guid.NewGuid(),
			CommandId = Guid.NewGuid(),
			SequenceId = GuidExtensions.CreateVersion7(),
			TargetId = Guid.NewGuid(),
			TargetTypeId = Guid.NewGuid(),
			CollectionId = Guid.NewGuid(),
			ComponentTypeId = Guid.NewGuid(),
			ComponentId = Guid.NewGuid(),
		};
		var buffer = new byte[1024];
		var written = 0;
		writer.Serialize<object>(buffer, ref written, added);

		var read = 0;
		var back = (ComponentAddedEvent)schema.CreateReader(header).Deserialize<object>(buffer, ref read);

		await Assert.That(header.Types.Single(x => x.MapId == -66).Schema).IsEqualTo("1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid ComponentTypeId Guid ComponentId Guid Data object?");
		await Assert.That(back.ComponentId).IsEqualTo(added.ComponentId);
		await Assert.That(back.TargetId).IsEqualTo(added.TargetId);
		await Assert.That(back.SequenceId).IsEqualTo(default(Guid)).Because("2026.405 has no SequenceId: it was never written");
		await Assert.That(read).IsEqualTo(written);
	}

	[Test]
	public async Task Should_refuse_a_signature_amended_in_place()
	{
		var schema = CreateSchema();
		var header = schema.DescribeSnapshot(schema.CreateWriter(), _stream, _snapshot, _watermark, entityCount: 0);
		var added = header.Types.Single(x => x.MapId == -66);
		added.Schema += " Note string?"; // same version, one more field: a layout this build does not have

		var refused = Assert.Throws<SnapshotSchemaRefusedException>(() => schema.CreateReader(header));

		await Assert.That(refused.Entry).IsSameReferenceAs(added);
	}

	[Test]
	public async Task Should_refuse_a_version_the_type_does_not_list()
	{
		var schema = CreateSchema();
		var header = schema.DescribeSnapshot(schema.CreateWriter(), _stream, _snapshot, _watermark, entityCount: 0);
		var added = header.Types.Single(x => x.MapId == -66);
		added.Version = 2099.001; // a newer build's schema

		var refused = Assert.Throws<SnapshotSchemaRefusedException>(() => schema.CreateReader(header));

		await Assert.That(refused.Entry).IsSameReferenceAs(added);
	}

	[Test]
	public async Task Should_refuse_a_type_this_build_does_not_map()
	{
		var schema = CreateSchema();
		var header = schema.DescribeSnapshot(schema.CreateWriter(), _stream, _snapshot, _watermark, entityCount: 0);
		var unknown = new SnapshotType { MapId = 400, TypeId = Guid.NewGuid(), Version = 2026.1, Schema = "1 Value string" };
		header.Types.Add(unknown);

		var refused = Assert.Throws<SnapshotSchemaRefusedException>(() => schema.CreateReader(header));

		await Assert.That(refused.Entry).IsSameReferenceAs(unknown);
	}

	[Test]
	public async Task Should_refuse_another_format()
	{
		var schema = CreateSchema();
		var header = schema.DescribeSnapshot(schema.CreateWriter(), _stream, _snapshot, _watermark, entityCount: 0);
		header.FormatVersion = schema.FormatVersion + 1;

		var refused = Assert.Throws<SnapshotSchemaRefusedException>(() => schema.CreateReader(header));

		await Assert.That(refused.Entry).IsNull();
	}

	[Test]
	public async Task Should_carry_the_header_and_a_chunk_over_sbx()
	{
		var schema = CreateSchema();
		var header = schema.DescribeSnapshot(schema.CreateWriter(), _stream, _snapshot, _watermark, entityCount: 4777);
		var chunk = new SnapshotChunk { SnapshotId = _snapshot, Index = 3, Count = 2, Payload = [0x01, 0xFE, 0x00, 0x80] };

		var headerBack = (SnapshotBegin)RoundTrip(schema, header);
		var chunkBack = (SnapshotChunk)RoundTrip(schema, chunk);

		await Assert.That(headerBack.Watermark).IsEqualTo(_watermark);
		await Assert.That(headerBack.EntityCount).IsEqualTo(4777L);
		await Assert.That(headerBack.TimeBase).IsEqualTo(header.TimeBase);
		await Assert.That(headerBack.Types.Select(x => x.ToString())).IsEquivalentTo(header.Types.Select(x => x.ToString()));
		await Assert.That(chunkBack.Index).IsEqualTo(3L);
		await Assert.That(chunkBack.Payload).IsEquivalentTo(chunk.Payload);
	}

	[Test]
	public async Task Should_carry_the_snapshot_frames_over_json()
	{
		TransportOperation[] frames =
		[
			new SnapshotChunk { SnapshotId = _snapshot, Index = 0, Count = 1, Payload = [0xAB] },
			new SnapshotEnd { SnapshotId = _snapshot, Watermark = _watermark, ChunkCount = 1, RecordCount = 1 },
			new SnapshotDeclined { StreamId = _stream, Reason = "none served" },
		];

		var back = frames.Select(x => JsonSerializer.Deserialize<TransportOperation>(JsonSerializer.Serialize(x, AppJsonContext.Default.TransportOperation), AppJsonContext.Default.TransportOperation)!).ToArray();

		await Assert.That(back.Select(x => x.GetType())).IsEquivalentTo(frames.Select(x => x.GetType()));
		await Assert.That(((SnapshotChunk)back[0]).Payload).IsEquivalentTo(new byte[] { 0xAB });
		await Assert.That(((SnapshotDeclined)back[2]).Reason).IsEqualTo("none served");
	}

	SnapshotSchema CreateSchema()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSbxSerializer();
		return new SnapshotSchema(services.BuildServiceProvider().GetRequiredService<ISbxSerializerFactory>());
	}

	TransportOperation RoundTrip(SnapshotSchema schema, TransportOperation frame)
	{
		var bytes = schema.CreateWriter().SerializeFrame<TransportOperation>(frame, new byte[256]);
		var pos = 0;
		return schema.CreateWriter().Deserialize<TransportOperation>(bytes.AsSpan(), ref pos);
	}
}
