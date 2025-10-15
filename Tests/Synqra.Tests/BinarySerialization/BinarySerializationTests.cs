using Synqra.BinarySerializer;
using Synqra.Tests.SampleModels;
using Synqra.Tests.SampleModels.Binding;
using Synqra.Tests.SampleModels.Serialization;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Assertions.Extensions;
using static Synqra.BinarySerializer.SBXSerializer;

namespace Synqra.Tests.BinarySerialization;

#pragma warning disable TUnitAssertions0002 // Assert statements must be awaited

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
		int pos3 = 0;
		ser.Serialize(serBuf, data, ref pos3);
	}
}

internal class BinarySerializationObjectPropertyTests : BaseTest
{
	[Test]
	public void Should_10_serialize_generic_int_without_type()
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[1024];
		int pos = 0;

		int data = 4;

		ser.Serialize<int>(buffer, data, ref pos, emitTypeId: false);

		buffer = buffer[0..pos];
		HexDump(buffer);

		var pos2 = 0;
		var deserialized = ser.Deserialize<int>(buffer, ref pos2, consumeTypeId: false);

		Assert.That(deserialized).IsEquivalentTo(data).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer)).IsEqualTo("08").GetAwaiter().GetResult();
	}

	[Test]
	public void Should_10_serialize_generic_int_with_type()
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[1024];
		int pos = 0;

		int data = 4;

		ser.Serialize<int>(buffer, data, ref pos, emitTypeId: true);

		buffer = buffer[0..pos];
		HexDump(buffer);

		var pos2 = 0;
		var deserialized = ser.Deserialize<int>(buffer, ref pos2, consumeTypeId: true);

		Assert.That(deserialized).IsEquivalentTo(data).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer)).IsEqualTo("0108").GetAwaiter().GetResult();
	}

	[Test]
	public void Should_10_serialize_boxed_int()
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[1024];
		int pos = 0;

		int data = 4;

		ser.Serialize<object>(buffer, data, ref pos);

		buffer = buffer[0..pos];
		HexDump(buffer);

		var pos2 = 0;
		var deserialized = (int)(long)ser.Deserialize<object>(buffer, ref pos2);

		Assert.That(deserialized).IsEqualTo(data).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer)).IsEqualTo("0308").GetAwaiter().GetResult();
	}

	public static IEnumerable<Func<(int, object, string)>> Should_20_serialize_sample_1_model_source()
	{
		yield return () => (11, new SampleFieldSealedModel() { Data = new SampleSealedModel { Id = 5 } }, "020A");
		yield return () => (12, new SampleFieldBaseModel() { Data = new SampleBaseModel { Id = 5 } }, "06010A");
		yield return () => (13, new SampleFieldBaseModel() { Data = new SampleDerivedModel { Id = 6, DerId = 4 } }, "060A080C");
		yield return () => (14, new SampleFieldObjectModel() { Data = new SampleBaseModel { Id = 5 } }, "0C080A");
		yield return () => (15, new SampleFieldListIntModel() { Data = [1, 2, 3] }, "1403020406");
		yield return () => (16, new SampleFieldListSealedModel() { Data = [new SampleSealedModel { Id = 5 }] }, "16010A");

		yield return () => (21, new SampleFieldListBaseModel() { Data = [] }/*                                                                          */, "180F");     // List_R_E // No, I still need typeID here. Yes, it is not covariant, but it is not sealed either, but items can be derived...
		yield return () => (22, new SampleFieldListBaseModel() { Data = [new SampleBaseModel /*   */ { Id = 5 }, new SampleBaseModel /*   */ { Id = 5 }] }, "1811020A0A"); // List_R_R
		yield return () => (23, new SampleFieldListBaseModel() { Data = [new SampleDerivedModel /**/ { Id = 5 }, new SampleDerivedModel /**/ { Id = 5 }] }, "18130A02000A000A");   // List_R_S
		yield return () => (24, new SampleFieldListBaseModel() { Data = [new SampleBaseModel /*   */ { Id = -1 }, new SampleDerivedModel /**/ { Id = 6 }] }, "18150208010A000C");   // List_R_H

		yield return () => (31, new SampleFieldEnumerableBaseModel() { Data = new List<SampleBaseModel>/**/{ /*                                                                */ } }, "1A0F"); // List_S_E
		yield return () => (32, new SampleFieldEnumerableBaseModel() { Data = new List<SampleBaseModel>/**/{ new SampleBaseModel { Id = 5 }, new SampleBaseModel { Id = 5 }       } }, "1A11020A0A"); // List_S_R
		yield return () => (33, new SampleFieldEnumerableBaseModel() { Data = new List<SampleBaseModel>/**/{ new SampleDerivedModel { DerId = 3, Id = 5 }, new SampleDerivedModel { DerId = 3, Id = 5 } } }, "1A130A02060A060A"); // List_S_S
		yield return () => (34, new SampleFieldEnumerableBaseModel() { Data = new List<SampleBaseModel>/**/{ new SampleBaseModel { Id = 5 }, new SampleDerivedModel { DerId = 2, Id = 5 }    } }, "1A1502080A0A040A"); // List_S_H
		yield return () => (35, new SampleFieldEnumerableBaseModel() { Data = new List<SampleDerivedModel> { /*                                                                */ } }, "1A170A"); // List_S_E
		yield return () => (37, new SampleFieldEnumerableBaseModel() { Data = new List<SampleDerivedModel> { new SampleDerivedModel { Id = 5 }, new SampleDerivedModel { Id = 5 } } }, "1A190A02000A000A"); // List_S_S
		// Todo: Add DerivedDerivedModel to make use of ElementType flags

		yield return () => (45, new SampleFieldObjectModel() { Data = new List<SampleBaseModel> { } } /*                                                                */, "0C1708"); // List_S_E
		yield return () => (46, new SampleFieldObjectModel() { Data = new List<SampleBaseModel> { new SampleBaseModel { Id = 5 }, new SampleBaseModel { Id = 5 } } }, "0C1908020A0A"); // List_S_R
		yield return () => (47, new SampleFieldObjectModel() { Data = new List<SampleBaseModel> { new SampleDerivedModel { Id = 5 }, new SampleDerivedModel { Id = 5 } } }, "0C1B080A02000A000A"); // List_S_S
		yield return () => (48, new SampleFieldObjectModel() { Data = new List<SampleBaseModel> { new SampleBaseModel { Id = 5 }, new SampleDerivedModel { Id = 5 } } }, "0C1D0802080A0A000A"); // List_S_H

		yield return () => (51, new SampleFieldListIntModel() { Data = new List<int> { } } /*    */, "1400"); // no list type id but count 0
		yield return () => (52, new SampleFieldListIntModel() { Data = new List<int> { 5, 6 } }/**/, "14020A0C");
		yield return () => (53, new SampleFieldListSealedModel() { Data = new List<SampleSealedModel> { } } /*                                                              */, "1600"); // no list type id but count 0
		yield return () => (54, new SampleFieldListSealedModel() { Data = new List<SampleSealedModel> { new SampleSealedModel { Id = 5 }, new SampleSealedModel { Id = 5 } } }, "16020A0A");

		// Dictionary                                                                                                                                                                     A=  int1  B=  SSM5
		yield return () => (61, new SampleFieldDictionaryStringObjectModel() { Data = new Dictionary<string, object> { { "A", 1 }, { "B", new SampleSealedModel { Id = 5 } } } }, "1C 02 4100 0302 4200 040A");

		// Prod
		/*
		yield return () => (70, new NewEvent1
		{
			Event = new CommandCreatedEvent
			{
				CommandId = default,
				EventId = default,
				Data = new CreateObjectCommand
				{
					CommandId = default,
					ContainerId = default,
					CollectionId = default,
					Target = null,
					TargetId = default,
					TargetTypeId = default,
				},
			},
		}, "1C 02 4100 0302 4200 040A");
		*/
	}

	[Test]
	[MethodDataSource(typeof(BinarySerializationObjectPropertyTests), nameof(Should_20_serialize_sample_1_model_source))]
	public async Task Should_20_serialize_sample_1_model(int id, object model, string expectedHex)
	{
		var modelJsonX = JsonSerializer.Serialize(model, new JsonSerializerOptions(SampleJsonSerializerContext.Default.Options) { WriteIndented = true });
		Console.WriteLine(modelJsonX);

		var ser = new SBXSerializer();
		ser.Map( 1, 1, typeof(SampleFieldSealedModel)); // 0z02
		ser.Map( 2, 1, typeof(SampleSealedModel)); // 0z04
		ser.Map( 3, 1, typeof(SampleFieldBaseModel)); // 0z06
		ser.Map( 4, 1, typeof(SampleBaseModel));
		ser.Map( 5, 1, typeof(SampleDerivedModel)); // 0z1A
		ser.Map( 6, 1, typeof(SampleFieldObjectModel)); // 0z1C
		ser.Map( 7, 1, typeof(SampleFieldDerrivedModel));
		ser.Map( 8, 1, typeof(SampleFieldIntModel));
		ser.Map( 9, 1, typeof(SampleFieldSealedDerivedModel));
		ser.Map(10, 1, typeof(SampleFieldListIntModel));
		ser.Map(11, 1, typeof(SampleFieldListSealedModel));
		ser.Map(12, 1, typeof(SampleFieldListBaseModel));
		ser.Map(13, 1, typeof(SampleFieldEnumerableBaseModel));
		ser.Map(14, 1, typeof(SampleFieldDictionaryStringObjectModel));

		Span<byte> buffer = stackalloc byte[1024];
		int pos = 0;


		ser.Serialize<object>(buffer, model, ref pos);

		buffer = buffer[0..pos];
		HexDump(buffer);

		var pos2 = 0;
		var deserialized = ser.Deserialize<object>(buffer, ref pos2);

		var deserializedJsonX = JsonSerializer.Serialize(deserialized, new JsonSerializerOptions(SampleJsonSerializerContext.Default.Options) { WriteIndented = true });
		Console.WriteLine(deserializedJsonX);

		var modelJson = JsonSerializer.Serialize(model, new JsonSerializerOptions(SampleJsonSerializerContext.Default.Options) { WriteIndented = false });
		var deserializedJson = JsonSerializer.Serialize(deserialized, new JsonSerializerOptions(SampleJsonSerializerContext.Default.Options) { WriteIndented = false });

		Assert.That(deserializedJson).IsEqualTo(modelJson).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer)).IsEqualTo(expectedHex.Replace(" ", "")).GetAwaiter().GetResult();
	}

	[Test]
	[Property("CI", "false")]
	public void Should_reserve_list_type_id_range()
	{
		Console.WriteLine(".");
		var count = (int)ListTypeId.MAX;
		Assert.That(count).IsEqualTo(16).GetAwaiter().GetResult();
		var ser = new SBXSerializer();
		for (int i = 0; i < count; i++)
		{
			var listTypeId = (ListTypeId)(i);
			var typeId = (TypeId)(TypeId.ListTypeFrom - i);
			Span<Byte> buffer = stackalloc byte[1];
			int pos = 0;
			ser.Serialize(buffer, (long)typeId, ref pos);
			Console.WriteLine($"{i,2} {listTypeId,12} <--> {(int)typeId,3} 0z{buffer[0],2:X2} {typeId,12}"); // z - for ZigZag
			Assert.That(typeId.ToString()).Contains(listTypeId.ToString()).GetAwaiter().GetResult();

			Assert.That(typeId.ListTypeId()).IsEqualTo(listTypeId).GetAwaiter().GetResult();
			Assert.That(listTypeId.TypeId()).IsEqualTo(typeId).GetAwaiter().GetResult();
		}
		Assert.That(TypeId.ListTypeTo).IsEqualTo(TypeId.SpecList_S_H).GetAwaiter().GetResult();
		Assert.That((int)TypeId.ListTypeTo).IsEqualTo((int)(TypeId.ListTypeFrom - (int)(ListTypeId.MAX - 1))).GetAwaiter().GetResult();
	}
}

internal class BinarySerializationListDictionaryTests : BaseTest
{
	[Test]
	public void Should_15_serialize_list_of_int()
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[1024];
		int pos = 0;

		var data = new List<uint> { 1, 2, 3 };

		ser.Serialize(buffer, data, ref pos);

		buffer = buffer[0..pos];
		HexDump(buffer);

		Assert.That(pos).IsEqualTo(4).GetAwaiter().GetResult();
		var pos2 = 0;
		var deserialized = ser.Deserialize<IEnumerable<uint>>(buffer, ref pos2);

		Assert.That(deserialized).IsEquivalentTo(data).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer)).IsEqualTo("03010203").GetAwaiter().GetResult();
	}

	[Test]
	public void Should_15_serialize_list_of_string()
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[1024];
		int pos = 0;

		var data = new List<string> { "One", "Two", "Three" };

		ser.Serialize(buffer, data, ref pos);

		buffer = buffer[0..pos];
		HexDump(buffer);

		Assert.That(pos).IsLessThan(20).GetAwaiter().GetResult();
		var pos2 = 0;
		var deserialized = ser.Deserialize<List<string>>(buffer, ref pos2);

		Assert.That(deserialized).IsEquivalentTo(data).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer)).IsEqualTo("03 4F6E6500 54776F00 546872656500".Replace(" ", "")).GetAwaiter().GetResult();
	}

	[Test]
	public void Should_17_make_string_interning()
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[1024];
		int pos = 0;

		var data = new List<string> { "Three", "", "Three" };

		ser.Serialize(buffer, data, ref pos);

		buffer = buffer[0..pos];
		HexDump(buffer);

		//Assert.That(pos).IsLessThan(20).GetAwaiter().GetResult();
		var pos2 = 0;
		var deserialized = ser.Deserialize<List<string>>(buffer, ref pos2);

		Assert.That(deserialized).IsEquivalentTo(data).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer)).IsEqualTo("03 546872656500 00 80".Replace(" ", "")).GetAwaiter().GetResult();
	}

	[Test]
	[Property("CI", "false")]
	// [Explicit] NEVER MARK AS EXPLICIT
	public void Should_20_serialize_dictionary_of_string()
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[1024];
		int pos = 0;

		var data = new Dictionary<string, string>
		{
			{ "Key1", "Value1" },
			{ "Key2", "Value2" },
			{ "Key3", "Value3" }
		};

		ser.Serialize(buffer, data, ref pos);

		buffer = buffer[0..pos];
		HexDump(buffer);

		Assert.That(pos).IsLessThan(40).GetAwaiter().GetResult();
		var pos2 = 0;
		var deserialized = ser.Deserialize<Dictionary<string, string>>(buffer, ref pos2);

		Assert.That(deserialized).IsEquivalentTo(data).GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer)).IsEqualTo("03 4B 65 79  31 00 56 61  6C 75 65 31  00 4B 65 79  32 00 56 61  6C 75 65 32  00 4B 65 79  33 00 56 61  6C 75 65 33  00 ".Replace(" ", "")).GetAwaiter().GetResult();
	}

	[Test]
	public void Should_print_cyrillic_utf8_codes()
	{
		var ser = new SBXSerializer();
		Span<byte> buffer = stackalloc byte[1024];
		int pos = 0;

		ser.Serialize(buffer, "п", ref pos);

		buffer = buffer[0..pos];
		HexDump(buffer);

		Assert.That(pos).IsLessThan(20).GetAwaiter().GetResult();
		var pos2 = 0;
		var deserialized = ser.Deserialize<string>(buffer, ref pos2);

		Assert.That(deserialized).IsEqualTo("п").GetAwaiter().GetResult();
		Assert.That(pos2).IsEqualTo(pos).GetAwaiter().GetResult();

		Assert.That(Convert.ToHexString(buffer)).IsEqualTo("D0BF00").GetAwaiter().GetResult();
	}
}

// [NotInParallel]
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

public class BinarySerializationTests : BaseTest
{

	[Test]
	public async Task Should_serialize_arbitrary_class_by_field_names_as_object()
	{
		// Arrange
		var testData = new SampleTestData
		{
			Id = 1,
			Name = "Test",
			// CreatedAt = DateTime.UtcNow,
			// Tags = new List<string> { "Tag1", "Tag2" }
		};

		var ser = new SBXSerializer();
		// Act
		Span<byte> buffer = stackalloc byte[10240];
		ReadOnlySpan<byte> rbuffer = buffer;
		int pos = 0;
		ser.Serialize<object>(buffer, testData, ref pos);
		buffer = buffer[..pos];

		// var hex = Convert.ToHexString(buffer.Slice(0, pos).ToArray());
		HexDump(buffer[..pos]);
		// Console.WriteLine(hex);
		// Console.WriteLine(Encoding.UTF8.GetString(buffer.Slice(0, pos)));

		pos = 0;
		var de = ser.Deserialize<SampleTestData>(in rbuffer, ref pos);
		await Assert.That(de.Id).IsEqualTo(testData.Id);
		await Assert.That(de.Name).IsEqualTo(testData.Name);
	}

	[Test]
	public async Task Should_serialize_arbitrary_class_by_field_names_as_known()
	{
		// Arrange
		var testData = new SampleTestData
		{
			Id = 1,
			Name = "Test",
			// CreatedAt = DateTime.UtcNow,
			// Tags = new List<string> { "Tag1", "Tag2" }
		};

		var ser = new SBXSerializer();
		// Act
		Span<byte> buffer = stackalloc byte[10240];
		ReadOnlySpan<byte> rbuffer = buffer;
		int pos = 0;
		ser.Serialize(buffer, testData, ref pos);
		buffer = buffer[..pos];

		// var hex = Convert.ToHexString(buffer.Slice(0, pos).ToArray());
		HexDump(buffer[..pos]);
		HexDump(Encoding.ASCII.GetBytes(JsonSerializer.Serialize(testData, new JsonSerializerOptions(SampleJsonSerializerContext.Default.Options)
		{
			WriteIndented = false
		})));
		// Console.WriteLine(hex);
		// Console.WriteLine(Encoding.UTF8.GetString(buffer.Slice(0, pos)));

		pos = 0;
		var de = ser.Deserialize<SampleTestData>(in rbuffer, ref pos);
		await Assert.That(de.Id).IsEqualTo(testData.Id);
		await Assert.That(de.Name).IsEqualTo(testData.Name);
	}

	[Test]
	public async Task Should_serialize_generated_class_by_field_names_as_known()
	{
		// Arrange
		var ser = new SBXSerializer();
		var data = new SampleTestSynqraModel
		{
			Id = 7,
			Name = "The",
		};
		ser.Map(5, 2025.772, typeof(SampleTestSynqraModel));
		ser.Map(6, 2025.772, typeof(SamplePublicModel_));

		// Act
		Span<byte> buffer = stackalloc byte[10240];
		ReadOnlySpan<byte> rbuffer = buffer;
		int pos = 0;
		ser.Serialize(in buffer, data, ref pos);
		// buffer = buffer[..pos];

		// Assert
		HexDump(buffer[..pos]);
		Assert.That(pos).IsEqualTo(6).GetAwaiter().GetResult();

		pos = 0;
		var de = ser.Deserialize<SampleTestSynqraModel>(in rbuffer, ref pos);
		await Assert.That(de.Id).IsEqualTo(data.Id);
		await Assert.That(de.Name).IsEqualTo(data.Name);
		await Assert.That(pos).IsEqualTo(6);
	}

	[Test]
	[Property("CI", "false")]
	// [Explicit] NEVER MARK AS EXPLICIT
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
		ReadOnlySpan<byte> rbuffer = buffer;
		int pos = 0;
		ser.Serialize<TransportOperation>(buffer, data, ref pos);
		buffer = buffer[..pos];

		// Assert
		HexDump(buffer);
		pos = 0;
		var de = (NewEvent1)ser.Deserialize<TransportOperation>(in rbuffer, ref pos);
		var te = (ObjectCreatedEvent)de.Event;
		var te2 = (ObjectCreatedEvent)data.Event;
		await Assert.That(te.EventId).IsEqualTo(te2.EventId);
		await Assert.That(te.CommandId).IsEqualTo(te2.CommandId);
		await Assert.That(te.TargetId).IsEqualTo(te2.TargetId);
		await Assert.That(te.TargetTypeId).IsEqualTo(te2.TargetTypeId);
		await Assert.That(te.CollectionId).IsEqualTo(te2.CollectionId);
	}
}
