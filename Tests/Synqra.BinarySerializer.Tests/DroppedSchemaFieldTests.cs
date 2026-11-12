using Synqra.Tests.SampleModels.Serialization;

namespace Synqra.BinarySerializer.Tests;

// A record written under a schema that names a since-dropped member still decodes: the member's bytes are skipped by type.
public class DroppedSchemaFieldTests : SbxBaseTest
{
	readonly byte[] _buffer = new byte[1024];

	int Write(SbxSerializer writer, object model)
	{
		var pos = 0;
		writer.Serialize<object>(_buffer, ref pos, model);
		HexDump(_buffer.AsSpan(0, pos));
		return pos;
	}

	(object Result, int Read) Read(SbxSerializer reader, int written)
	{
		var pos = 0;
		var result = reader.Deserialize<object>(_buffer.AsSpan(0, written), ref pos);
		return (result, pos);
	}

	[Test]
	public async Task Should_read_a_record_of_the_old_schema_past_the_dropped_field()
	{
		var writer = new SbxSerializer();
		writer.Map(1, typeof(SampleDroppedFieldWriterModel));
		var reader = new SbxSerializer();
		reader.Map(1, 1.0, typeof(SampleDroppedFieldReaderModel));

		var written = Write(writer, new SampleDroppedFieldWriterModel
		{
			A = new Guid("0199eeb4-33df-7430-a02a-d9fd1bf80001"),
			B = "kept",
		});
		var (result, read) = Read(reader, written);

		await Assert.That(read).IsEqualTo(written);
		var back = result as SampleDroppedFieldReaderModel;
		await Assert.That(back).IsNotNull();
		await Assert.That(back!.B).IsEqualTo("kept");
	}

	[Test]
	public async Task Should_write_the_old_schema_with_a_default_in_the_dropped_slot()
	{
		var writer = new SbxSerializer();
		writer.Map(1, 1.0, typeof(SampleDroppedFieldReaderModel));
		var reader = new SbxSerializer();
		reader.Map(1, typeof(SampleDroppedFieldWriterModel));

		var written = Write(writer, new SampleDroppedFieldReaderModel
		{
			B = "kept",
		});
		var (result, read) = Read(reader, written);

		await Assert.That(read).IsEqualTo(written);
		var back = result as SampleDroppedFieldWriterModel;
		await Assert.That(back).IsNotNull();
		await Assert.That(back!.A).IsEqualTo(Guid.Empty);
		await Assert.That(back.B).IsEqualTo("kept");
	}
}
