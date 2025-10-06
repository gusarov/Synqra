using Synqra.BinarySerializer;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.BinarySerialization;

#pragma warning disable TUnitAssertions0002 // Assert statements must be awaited

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

[NotInParallel]
public class BinarySerializationTests : BaseTest
{

	[Test]
	public async Task Should_serialize_arbitrary_class_by_field_names()
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

}
