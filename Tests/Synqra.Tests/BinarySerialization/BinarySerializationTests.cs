using Synqra.BinarySerializer;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.BinarySerialization;

public class TestData
{
	public TestData()
	{

	}

	public int Id { get; set; }
	public string? Name { get; set; }
	// public DateTime CreatedAt { get; set; }
	// public List<string> Tags { get; set; }
}

[Schema(2025.09, "0 Id int Name str?")]
[Schema(2025.772, "1 Id int Name string?")]
public partial class TestSynqraModel
{
	public partial int Id { get; set; }
	public partial string? Name { get; set; }
}

#pragma warning disable TUnitAssertions0002 // Assert statements must be awaited

[NotInParallel]
internal class BinarySerializationGuidTests : BaseTest
{
	[Test]
	[Arguments(1, "00000000-0000-0000-0000-000000000000", "00")] // glyph zero
	[Arguments(2, "ffffffff-ffff-ffff-ffff-ffffffffffff", "01")] // glyph one
	[Arguments(3, "DB7196E3-FD0C-4C15-8D5A-94ABC2D5DFDC", "8D E39671DB 0CFD 154C 5A 94ABC2D5DFDC")] // regular Guid v4
	[Arguments(4, "DB7196E3-FD0C-4C15-8D5A-94ABC2D50000", "8D E39671DB 0CFD 154C 5A 94ABC2D50000")] // TODO compress it!
	[Arguments(5, "0199bf3f-deae-77bb-8631-335347819f65", "07 BC01 BB 46 31335347819F65")] // v7 compressed related to known time base
	[Arguments(6, "00000000-0037-8000-8000-000000000000", "28 3700")] // compress with presence mask
	[Arguments(7, "DB7196E3-FD0C-AC15-0D5A-94ABC2D5DFDC", "02 E39671DB 0CFD 15AC 0D5A 94ABC2D5DFDC")] // Apollo 1980 Guid
	[Arguments(8, "DB7196E3-FD0C-AC15-C0DA-94ABC2D5DFDC", "02 E39671DB 0CFD 15AC C0DA 94ABC2D5DFDC")] // Microsoft 1990 Guid
	public void Should_serialize_guid_with_test_vectors(int id, string guidString, string hex)
	{
#if NET9_0_OR_GREATER
		// Console.WriteLine(Guid.CreateVersion7());
#endif

		var guid = new Guid(guidString);
		Span<byte> buffer = stackalloc byte[20];
		var ser = new SBXSerializer();
		ser.SetTimeBase(new DateTime(2025, 10, 7, 15, 17, 38, DateTimeKind.Utc));
		int pos = 0;
		ser.Serialize(buffer, guid, ref pos);
		var pos2 = 0;
		HexDump(buffer.Slice(0, pos));
		var deserialized = ser.DeserializeGuid(buffer[0..pos], ref pos2);
		Assert.That(deserialized).IsEqualTo(guid).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer.Slice(0, pos))).IsEqualTo(hex.Replace(" ", "")).GetAwaiter().GetResult();
	}

}

[NotInParallel]
internal class BinarySerializationSignedTests : BaseTest
{
	[Test]
	[Arguments(1,0, "00")]
	[Arguments(2,-1, "01")]
	[Arguments(3,1, "02")]
	[Arguments(4,63, "7E")]
	[Arguments(5,-64, "7F")]
	[Arguments(6,64, "8001")]
	[Arguments(7,-65, "8101")]
	[Arguments(8,65, "8201")]
	[Arguments(9,127, "FE01")]
	[Arguments(11,-128, "FF01")]
	[Arguments(12,128, "8002")]
	[Arguments(13,-129, "8102")]
	/*
	[Arguments(14,-32766, "")]
	[Arguments(15,-32767, "")]
	[Arguments(16,-32768, "")]
	[Arguments(17,32766, "")]
	[Arguments(18,32767, "")]
	[Arguments(19,32768, "")]
	*/
	public void Should_serialize_signed_integers_with_test_vectors(int id, int i, string hex)
	{
		Span<byte> buffer = stackalloc byte[10];
		var ser = new SBXSerializer();
		int pos = 0;
		ser.Serialize(buffer, i, ref pos);
		Assert.That(pos).IsLessThan(4).GetAwaiter().GetResult();
		var pos2 = 0;
		var deserialized = ser.DeserializeSigned(buffer, ref pos2);
		Assert.That(deserialized).IsEqualTo(i).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer.Slice(0, pos))).IsEqualTo(hex).GetAwaiter().GetResult();
	}

	[Test]
	[Arguments(-32766)]
	[Arguments(-32767)]
	[Arguments(-32768)]
	[Arguments(32766)]
	[Arguments(32767)]
	[Arguments(32768)]
	public void Should_serialize_signed_integers(int i)
	{
		Span<byte> buffer = stackalloc byte[10];
		int pos = 0;
		var ser = new SBXSerializer();
		ser.Serialize(buffer, i, ref pos);
		ReadOnlySpan<byte> buffer2 = buffer;
		var deserialized = ser.DeserializeSigned(ref buffer2);
		Assert.That(deserialized).IsEqualTo(i).GetAwaiter().GetResult();
		Assert.That(pos).IsLessThan(4).GetAwaiter().GetResult();
		Assert.That(pos).IsEqualTo(buffer.Length - buffer2.Length).GetAwaiter().GetResult();
	}

	[Test]
	public void Should_serialize_signed_integers()
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[10];
		for (int i = short.MinValue; i <= short.MaxValue; i++)
		{
			int pos = 0;
			ser.Serialize(buffer, i, ref pos);
			ReadOnlySpan<byte> buffer2 = buffer;
			var deserialized = ser.DeserializeSigned(ref buffer2);
			Assert.That(deserialized).IsEqualTo(i).GetAwaiter().GetResult();
			Assert.That(pos).IsLessThan(4).GetAwaiter().GetResult();
			Assert.That(pos).IsEqualTo(buffer.Length - buffer2.Length).GetAwaiter().GetResult();
		}
	}
}

[NotInParallel]
internal class BinarySerializationStringTests : BaseTest
{
	[Test]
	// [Arguments("Hi", "024869")]
	[Arguments("Hi", "486900")]
	public void Should_serialize_strings_with_test_vectors(string data, string hex)
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[20];
		int pos = 0;
		ser.Serialize(buffer, data, ref pos);
		Assert.That(pos).IsLessThan(4).GetAwaiter().GetResult();
		var pos2 = 0;
		var deserialized = ser.DeserializeString(buffer, ref pos2);
		Assert.That(deserialized).IsEqualTo(data).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer.Slice(0, pos))).IsEqualTo(hex).GetAwaiter().GetResult();

		var serBuf = buffer;
		ser.Serialize(ref serBuf, data);
	}
}

[NotInParallel]
internal class BinarySerializationUnsignedTests : BaseTest
{
	[Test]
	[Arguments(0x00u, "00")]
	[Arguments(0x7Fu, "7F")]
	[Arguments(0x80u, "8001")]
	[Arguments(0x81u, "8101")]
	[Arguments(0xFFu, "FF01")]
	public void Should_serialize_unsigned_integers_with_test_vectors(uint i, string hex)
	{
		Span<byte> buffer = stackalloc byte[10];
		var ser = new SBXSerializer();
		int pos = 0;
		ser.Serialize(buffer, i, ref pos);
		Assert.That(pos).IsLessThan(4).GetAwaiter().GetResult();
		var pos2 = 0;
		var deserialized = ser.DeserializeUnsigned(buffer, ref pos2);
		Assert.That(deserialized).IsEqualTo(i).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer.Slice(0, pos))).IsEqualTo(hex).GetAwaiter().GetResult();
	}

	[Test]
	public void Should_serialize_unsigned_integers()
	{
		Span<byte> buffer = stackalloc byte[10];
		var ser = new SBXSerializer();
		for (uint i = 0; i < ushort.MaxValue; i++)
		{
			int pos = 0;
			ser.Serialize(buffer, i, ref pos);
			Assert.That(pos).IsLessThan(4).GetAwaiter().GetResult();
			var pos2 = 0;
			var deserialized = ser.DeserializeUnsigned(buffer, ref pos2);
			Assert.That(deserialized).IsEqualTo(i).GetAwaiter().GetResult();
			Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();
		}
	}
	/*
	[Test]
	public async Task Should_serialize_unsigned_integers_quickly1()
	{
		var ser = new SBXSerializer();

		var ops = 1024 * MeasureOps(() =>
		{
			Span<byte> buffer = stackalloc byte[10];
			int pos = 0;
			// for (int i = 0; i < 1024; i++)
			{
				ser.Serialize(buffer, 777U, ref pos);
			}
		});
		Console.WriteLine(ops);
	}

	[Test]
	public async Task Should_serialize_unsigned_integers_quickly2()
	{
		var ser = new SBXSerializer();

		MeasureOps(() =>
		{
			// Span<byte> bufferOrig = stackalloc byte[10];
			Span<byte> buffer = stackalloc byte[10];
			// for (int i = 0; i < 1024; i++)
			{
				ser.Serialize(ref buffer, 777U);
			}
		});
	}

	[Test]
	public async Task Should_deserialize_unsigned_integers_quickly1()
	{
		var ser = new SBXSerializer();
		var buf = new byte[10];
		Span<byte> buffer = buf;
		ser.Serialize(ref buffer, 777U);

		var res = MeasureOps(() =>
		{
			ReadOnlySpan<byte> buffer = buf;
			int pos = 0;
			ser.DeserializeUnsigned(buffer, ref pos);
		});
		EmergencyLog.Default.Message($"Should_deserialize_unsigned_integers_quickly1 " + res);
	}

	[Test]
	public async Task Should_deserialize_unsigned_integers_quickly2()
	{
		var ser = new SBXSerializer();
		var buf = new byte[10];
		Span<byte> buffer = buf;
		ser.Serialize(ref buffer, 777U);

		var res = MeasureOps(() =>
		{
			ReadOnlySpan<byte> buffer = buf;
			ser.DeserializeUnsigned(ref buffer);
		});
		EmergencyLog.Default.Message($"Should_deserialize_unsigned_integers_quickly2 " + res);
	}
	*/
}

[NotInParallel]
public class BinarySerializationTests : BaseTest
{

	[Test]
	public async Task Should_serialize_arbitrary_class_by_field_names_as_object()
	{
		// Arrange
		var testData = new TestData
		{
			Id = 1,
			Name = "Test",
			// CreatedAt = DateTime.UtcNow,
			// Tags = new List<string> { "Tag1", "Tag2" }
		};

		var ser = new SBXSerializer();
		// Act
		Span<byte> buffer = stackalloc byte[10240];
		int pos = 0;
		ser.Serialize<object>(buffer, testData, ref pos);
		buffer = buffer[..pos];

		// var hex = Convert.ToHexString(buffer.Slice(0, pos).ToArray());
		HexDump(buffer[..pos]);
		// Console.WriteLine(hex);
		// Console.WriteLine(Encoding.UTF8.GetString(buffer.Slice(0, pos)));

		var de = (TestData)ser.Deserialize(ref buffer, typeof(TestData) /* just to help with NativeAOT. It will not use that type in code flow, because it will not read -1 */);
		await Assert.That(de.Id).IsEqualTo(testData.Id);
		await Assert.That(de.Name).IsEqualTo(testData.Name);
	}

	[Test]
	public async Task Should_serialize_arbitrary_class_by_field_names_as_known()
	{
		// Arrange
		var testData = new TestData
		{
			Id = 1,
			Name = "Test",
			// CreatedAt = DateTime.UtcNow,
			// Tags = new List<string> { "Tag1", "Tag2" }
		};

		var ser = new SBXSerializer();
		// Act
		Span<byte> buffer = stackalloc byte[10240];
		int pos = 0;
		ser.Serialize(buffer, testData, ref pos);
		buffer = buffer[..pos];

		// var hex = Convert.ToHexString(buffer.Slice(0, pos).ToArray());
		HexDump(buffer[..pos]);
		Console.WriteLine();
		HexDump(Encoding.ASCII.GetBytes(JsonSerializer.Serialize(testData, new JsonSerializerOptions(TestJsonSerializerContext.Default.Options)
		{
			WriteIndented = false
		})));
		// Console.WriteLine(hex);
		// Console.WriteLine(Encoding.UTF8.GetString(buffer.Slice(0, pos)));

		var de = ser.Deserialize<TestData>(ref buffer);
		await Assert.That(de.Id).IsEqualTo(testData.Id);
		await Assert.That(de.Name).IsEqualTo(testData.Name);
	}

	[Test]
	public async Task Should_serialize_generated_class_by_field_names_as_known()
	{
		// Arrange
		var ser = new SBXSerializer();
		var data = new TestSynqraModel
		{
			Id = 7,
			Name = "The",
		};
		ser.Map(5, 2025.772, typeof(TestSynqraModel));
		ser.Map(6, 2025.772, typeof(SamplePublicModel_));

		// Act
		Span<byte> buffer = stackalloc byte[10240];
		int pos = 0;
		ser.Serialize(buffer, data, ref pos);
		buffer = buffer[..pos];

		// Assert
		HexDump(buffer[..pos]);
		var de = ser.Deserialize<TestSynqraModel>(ref buffer);
		await Assert.That(de.Id).IsEqualTo(data.Id);
		await Assert.That(de.Name).IsEqualTo(data.Name);
		await Assert.That(pos).IsEqualTo(6);
	}

	[Test]
	public async Task Should_serialize_well_known_class_by_field_names_as_known2()
	{
		// Arrange
		var ser = new SBXSerializer();
		var data = new NewEvent1
		{
			Event = new ObjectCreatedEvent
			{
				EventId = default,
				CommandId = default,
				TargetId = default,
				TargetTypeId = default,
				CollectionId = default,
			},
		};
		ser.Map(1, 0, typeof(NewEvent1));
		ser.Map(2, 0, typeof(ObjectCreatedEvent));

		// Act
		Span<byte> buffer = stackalloc byte[10240];
		int pos = 0;
		ser.Serialize<TransportOperation>(buffer, data, ref pos);
		buffer = buffer[..pos];

		// Assert
		HexDump(buffer[..pos]);
		var de = (NewEvent1)ser.Deserialize<TransportOperation>(ref buffer);
		var te = (ObjectCreatedEvent)de.Event;
		var te2 = (ObjectCreatedEvent)data.Event;
		await Assert.That(te.EventId).IsEqualTo(te2.EventId);
		await Assert.That(te.CommandId).IsEqualTo(te2.CommandId);
		await Assert.That(te.TargetId).IsEqualTo(te2.TargetId);
		await Assert.That(te.TargetTypeId).IsEqualTo(te2.TargetTypeId);
		await Assert.That(te.CollectionId).IsEqualTo(te2.CollectionId);
	}
}
