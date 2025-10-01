using Synqra.Tests.TestHelpers;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

public class GuidExtensionsTests3 : BaseTest
{
	[Test]
	public unsafe async Task Should_show_binary_form()
	{
		var guid = new Guid("01997450-29df-72b8-89fc-a7dd0f52e420");
		Console.WriteLine(guid);

		var bytes = (byte*)&guid;
		for (int i = 0; i < 16; i++)
		{
			if (i % 2 == 0)
			{
				Console.Write("-");
			}
			Console.Write(bytes[i].ToString("x2"));
		}

		Console.WriteLine();
		var bytes2 = guid.ToByteArray();
		for (int i = 0; i < 16; i++)
		{
			if (i % 2 == 0)
			{
				Console.Write("-");
			}
			Console.Write(bytes2[i].ToString("x2"));
		}
	}

	// Test vectors from RFC 4122 and RFC 9562
	// https://www.rfc-editor.org/rfc/rfc9562.html#name-test-vectors
	[Test]
	[Arguments("C232AB00-9414-11EC-B3C8-9F6BDECED846", 1, 1)]
	[Arguments("5df41881-3aed-3515-88a7-2f4a814cf09e", 1, 3)]
	[Arguments("919108f7-52d1-4320-9bac-f847db4148a8", 1, 4)]
	[Arguments("2ed6657d-e927-568b-95e1-2665a8aea6a2", 1, 5)]
	[Arguments("1EC9414C-232A-6B00-B3C8-9F6BDECED846", 1, 6)]
	[Arguments("017F22E2-79B0-7CC3-98C4-DC0C0C07398F", 1, 7)]
	public async Task Should_detect_test_vectors(string testVector, int expectedVariant, int expectedVersion)
	{
		var guid = new Guid(testVector);
		await Assert.That(guid.GetVariant()).IsEqualTo(expectedVariant);
		await Assert.That(guid.GetVersion()).IsEqualTo(expectedVersion);
	}

	[Test]
	public async Task Should_handle_IUnknown()
	{
		var guid = new Guid("00000000-0000-0000-C000-000000000046"); // Microsoft COM IUnknown
		await Assert.That(guid.GetVariant()).IsEqualTo(2);
		var ex = Assert.Throws<Exception>(() => guid.GetVersion());
		Console.WriteLine(ex.GetType().Name);
		Console.WriteLine(ex.Message);
		await Assert.That(ex.Message).Contains("Variant");
	}

	[Test]
	public async Task Should_handle_ADO()
	{
		var guid = new Guid("{00000507-0000-0010-8000-00AA006D2EA4}"); // Microsoft ADO
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(0);
		// {F6D90F11-9C73-11D3-B32E-00C04F990BB4}
	}

	[Test]
	public async Task Should_handle_XML_DOM()
	{
		var guid = new Guid("{F6D90F11-9C73-11D3-B32E-00C04F990BB4}"); // Microsoft XML DOM
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(1);
		var dt = guid.GetTimestamp();
		Console.WriteLine(dt); // 1999-11-16 22:20:00
	}

	[Test]
	[Arguments(1, "5236-03-31 21:21:00")]
	// [Arguments(2, "5236-03-31 21:21:00")]
	[Arguments(6, "5236-03-31 21:21:00")]
	// [Arguments(7, "5236-03-31 21:21:00")] // overflow
	public async Task Should_show_max_time(byte version, string dateTimeStr)
	{
		DateTime dateTime = DateTime.Parse(dateTimeStr);
		var guid = new Guid("{FFFFFFFF-FFFF-0FFF-8FFF-FFFFFFFFFFFF}");
		unsafe
		{
			((byte*)&guid)[7] = (byte)(version << 4);
		}

		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(version);
		var dt = guid.GetTimestamp();
		Console.WriteLine(dt); // 5236-03-31 21:21:00
	}

#if NET8_0_OR_GREATER
	/* No Longer Possible
	[Test]
	[Arguments(7, "5236-03-31 21:21:00")]
	public async Task Should_show_max_uuid_dto(byte version, string expected)
	{
		var dateTime = DateTimeOffset.MaxValue;
		Span<byte> arr;
		unsafe
		{
			byte* bytes = (byte*)&dateTime;
			arr = new Span<byte>(bytes, 16);
		}
		Console.WriteLine(Convert.ToHexString(arr));

		var guid = GuidExtensions.CreateVersion7(dateTime);
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(version);

		Console.WriteLine(guid);

		var dt = guid.GetTimestamp();
		Console.WriteLine(dt); // 5236-03-31 21:21:00
	}


	[Test]
	[Arguments(7, "5236-03-31 21:21:00")]
	public async Task Should_show_max_uuid_dt(byte version, string expected)
	{
		var dateTime = DateTime.MaxValue;
		Span<byte> arr;
		unsafe
		{
			byte* bytes = (byte*)&dateTime;
			arr = new Span<byte>(bytes, 8);
		}
		Console.WriteLine(Convert.ToHexString(arr));

		var guid = GuidExtensions.CreateVersion7(dateTime);
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(version);

		Console.WriteLine(guid);

		var dt = guid.GetTimestamp();
		Console.WriteLine(dt); // 5236-03-31 21:21:00
	}

	[Test]
	[Arguments(7, "5236-03-31 21:21:00")]
	public async Task Should_show_max_ticks(byte version, string expected)
	{
		var dateTime = long.MaxValue;
		Span<byte> arr;
		unsafe
		{
			byte* bytes = (byte*)&dateTime;
			arr = new Span<byte>(bytes, 8);
		}
		Console.WriteLine(Convert.ToHexString(arr));

		var guid = GuidExtensions.CreateVersion7(dateTime);
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(version);

		Console.WriteLine(guid);

		var dt = guid.GetTimestamp();
		Console.WriteLine(dt); // 5236-03-31 21:21:00
	}
	*/

#endif

	[Test]
	public async Task Should_handle_v1_test_vector()
	{
		var guid = new Guid("C232AB00-9414-11EC-B3C8-9F6BDECED846");
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(1);

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		await Assert.That(timestamp).IsEqualTo(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)).ToUniversalTime().DateTime);
	}

	[Test]
	[Obsolete]
	public async Task Should_handle_v1_test_vector_create()
	{
		var guid = GuidExtensions.CreateVersion1(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)), clockSeq: 0x33C8, node: 0x9F6BDECED846);
		await Assert.That(guid.ToString()).IsEqualTo(new Guid("C232AB00-9414-11EC-B3C8-9F6BDECED846").ToString());
	}

	[Test]
	public async Task Should_handle_namespace_id_starting_vector()
	{
		var guid = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"); // see https://www.rfc-editor.org/rfc/rfc9562.html?utm_source=chatgpt.com#section-6.5
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(1);

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp); // 1998-02-04 22:13:53 (likely the date when Guid v3 was developed)
	}

	[Test]
	public async Task Should_handle_v3_test_vector()
	{
		var guid = GuidExtensions.CreateVersion3Dns("www.example.com");
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(3);

		var expected = new Guid("5df41881-3aed-3515-88a7-2f4a814cf09e");
		await Assert.That(guid.ToString()).IsEqualTo(expected.ToString());
	}

	[Test]
	public async Task Should_handle_v5_test_vector()
	{
		var guid = GuidExtensions.CreateVersion5Dns("www.example.com");
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(5);

		var expected = new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2");
		await Assert.That(guid.ToString()).IsEqualTo(expected.ToString());
	}

	[Test]
	public async Task Should_handle_v5_custom_online_vector()
	{
		var guid = GuidExtensions.CreateVersion5(new Guid("39771042-7f7c-40bf-bc79-c28d75f826ab"), "abc");

		await Assert.That(guid.ToString()).IsEqualTo(new Guid("c5c35eef-366a-510c-a735-1ffd99bc4304").ToString());
	}

	[Test]
	public async Task Should_handle_v6_test_vector()
	{
		var guid = new Guid("1EC9414C-232A-6B00-B3C8-9F6BDECED846");
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(6);

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		await Assert.That(timestamp).IsEqualTo(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)).ToUniversalTime().DateTime);
	}

	[Test]
	[Obsolete]
	public async Task Should_handle_v6_test_vector_create()
	{
		var guid = GuidExtensions.CreateVersion6(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)), 0x33C8, 0x9F6BDECED846);
		await Assert.That(guid.ToString()).IsEqualTo(new Guid("1EC9414C-232A-6B00-B3C8-9F6BDECED846").ToString());
	}

	[Test]
	public async Task Should_handle_v7_test_vector()
	{
		var guid = new Guid("017F22E2-79B0-7CC3-98C4-DC0C0C07398F");
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(7);

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		await Assert.That(timestamp).IsEqualTo(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)).ToUniversalTime().DateTime);
	}

	/* No Longer Possible
	[Test]
	public async Task Should_create_v7_fixed()
	{
		var guid = GuidExtensions.CreateVersion7(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)));
		Console.WriteLine(guid);
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(7);

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		await Assert.That(timestamp).IsEqualTo(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)).ToUniversalTime().DateTime);
	}
	*/

	[Test]
	public async Task Should_create_v7()
	{
		var guid = GuidExtensions.CreateVersion7();
		Console.WriteLine(guid);
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(7);

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		await Assert.That((DateTime.UtcNow - timestamp).TotalSeconds).IsLessThan(1);
	}

	[Test]
	public async Task Should_handle_v8_example_vector()
	{
		var guid = GuidExtensions.CreateVersion8_Sha256_Dns("www.example.com");
		await Assert.That(guid.GetVariant()).IsEqualTo(1);
		await Assert.That(guid.GetVersion()).IsEqualTo(8);

		var expected = new Guid("5c146b14-3c52-8afd-938a-375d0df1fbf6");
		await Assert.That(guid.ToString()).IsEqualTo(expected.ToString());
	}

	[Test]
	public async Task Should_compare_guids()
	{
		var arr = new byte[16];
		for (byte i = 0; i < 16; i++)
		{
			arr[i] = i;
		}
		Guid prev = FromNetworkOrder(arr);
		Console.WriteLine(prev);
		await Assert.That(prev.ToString()).IsEqualTo(new Guid("00010203-0405-0607-0809-0a0b0c0d0e0f").ToString());
		for (int i = 0; i < 16; i++)
		{
			arr[i]++;
			Guid newGuid = FromNetworkOrder(arr);
			//Console.WriteLine(newGuid);
			await Assert.That(newGuid.CompareTo(prev)).IsGreaterThan(0);
			await Assert.That(prev.CompareTo(newGuid)).IsLessThan(0);
			prev = newGuid;
		}
	}

	[Test]
	public async Task Should_compare_guids_2()
	{
		var arr = new byte[16];
		for (byte i = 0; i < 16; i++)
		{
			arr[i] = i;
		}
		Guid prev = FromNetworkOrder(arr);
		Console.WriteLine(prev);
		await Assert.That(prev.ToString()).IsEqualTo(new Guid("00010203-0405-0607-0809-0a0b0c0d0e0f").ToString());

		for (int i = 0; i < 15; i++)
		{
			arr[i]++;
			arr[i + 1]--;
			Guid newGuid = FromNetworkOrder(arr);
			arr[i + 1]++;
			Console.WriteLine(newGuid);
			await Assert.That(newGuid.CompareTo(prev)).IsGreaterThan(0);
			await Assert.That(prev.CompareTo(newGuid)).IsLessThan(0);
			prev = newGuid;
		}
	}

	[Test]
	public async Task Should_compare_guids_3()
	{
		var arr = new byte[16];
		for (byte i = 0; i < 16; i++)
		{
			arr[i] = (byte)(i + 16);
		}
		Guid prev = FromNetworkOrder(arr);
		await Assert.That(prev.ToString()).IsEqualTo(new Guid("10111213-1415-1617-1819-1a1b1c1d1e1f").ToString());

		for (int i = 0; i < 15; i++)
		{
			arr[i]++;
			arr[i + 1]--;
			Guid newGuid = FromNetworkOrder(arr);
			arr[i + 1]++;
			arr[i]--;
			Console.WriteLine(prev);
			Console.WriteLine(newGuid);
			Console.WriteLine();
			await Assert.That(newGuid.CompareTo(prev)).IsGreaterThan(0);
			await Assert.That(prev.CompareTo(newGuid)).IsLessThan(0);
		}
	}

	[Test]
	public async Task Should_compare_guids_4()
	{
		var arr1 = new byte[16];
		arr1[0] = 1;
		Guid a = FromNetworkOrder(arr1);
		var arr2 = new byte[16];
		arr2[1] = 1;
		Guid b = FromNetworkOrder(arr2);
		Console.WriteLine(a);
		Console.WriteLine(b);
		await Assert.That(a.CompareTo(b)).IsGreaterThan(0);
		await Assert.That(2.CompareTo(1)).IsGreaterThan(0); // assert our understanding of CompareTo
	}

	[Test]
	public async Task Should_compare_guids_5()
	{
		var bytes = new byte[8];
		var a = new Guid(0x00000100, 0, 0, bytes);
		Console.WriteLine(a);
		var b = new Guid(0x00000001, 0, 0, bytes);
		Console.WriteLine(b);
		await Assert.That(a.CompareTo(b)).IsGreaterThan(0);
		await Assert.That(2.CompareTo(1)).IsGreaterThan(0); // assert our understanding of CompareTo

		await Assert.That(new Guid(2, 0, 0, bytes).CompareTo(new Guid(1, 0, 0, bytes))).IsGreaterThan(0);
	}

#if !NETFRAMEWORK
	[Test]
	public async Task Should_understand_guids_1()
	{
		var arr = new byte[16];
		for (byte i = 0; i < 16; i++)
		{
			arr[i] = i;
		}
		Guid guid = FromNetworkOrder(arr);
		// Network Byte Order ToString representation
		await Assert.That(guid.ToString()).IsEqualTo("00010203-0405-0607-0809-0a0b0c0d0e0f");

		// Network Byte Order ToByteArray representation
		var arr1 = guid.ToByteArray(bigEndian: true);
		await Assert.That(Convert.ToHexString(arr1).ToLowerInvariant()).IsEqualTo("00010203-0405-0607-0809-0a0b0c0d0e0f".Replace("-", ""));

		// In Memory Representation
		string hexRam;
		unsafe
		{
			var span = new Span<byte>((byte*)&guid, sizeof(Guid));
			hexRam = Convert.ToHexString(span).ToLowerInvariant();
		}
		await Assert.That(hexRam).IsEqualTo("03020100-0504-0706-0809-0a0b0c0d0e0f".Replace("-", ""));

		// Integer
		arr = guid.ToByteArray(); // CurrentEndian (Little)
		int a = BitConverter.ToInt32(arr, 0); // Little
		await Assert.That(a).IsEqualTo(0x00010203);

		// Integer
		arr = guid.ToByteArray(bigEndian: true);
		a = BitConverter.ToInt32(arr, 0);
		await Assert.That(a).IsEqualTo(0x03020100);
	}
#endif

	Guid FromNetworkOrder([In] ReadOnlySpan<byte> span)
	{
#if NETFRAMEWORK
		var arr = span.ToArray();
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(arr, 0, 4);
			Array.Reverse(arr, 4, 2);
			Array.Reverse(arr, 6, 2);
		}
		return new Guid(arr);
#else
		return new Guid(span, bigEndian: true);
#endif
	}

#if NET9_0_OR_GREATER
	[Test]
	public async Task Should_create_v7_near_net()
	{
		Console.WriteLine(GuidExtensions.CreateVersion7());
		Console.WriteLine(Guid.CreateVersion7());
	}
#endif

	[Test]
	public async Task Should_create_v7_same_ms_sequence()
	{
		Guid prev = GuidExtensions.CreateVersion7();
		Console.WriteLine(prev);
		for (int i = 0; i < 24; i++)
		{
			var newGuid = GuidExtensions.CreateVersion7();
			Console.WriteLine(newGuid);
			await Assert.That(newGuid.CompareTo(prev)).IsGreaterThan(0);
			prev = newGuid;
		}
	}

	// [Test]
	public async Task Should_create_v7_manual_time_sequence()
	{
		/*
		var now = new DateTime(638939296991929998, DateTimeKind.Utc);
		Guid prev = GuidExtensions.CreateVersion7(now);
		Console.WriteLine(prev);
		for (int i = 0; i < 24; i++)
		{
			var newGuid = GuidExtensions.CreateVersion7(now);
			Console.WriteLine(newGuid);
			await Assert.That(newGuid.CompareTo(prev)).IsGreaterThan(0);
			prev = newGuid;
		}
		*/
	}

	[Test]
	[Category("Performance")]
	public async Task Should_create_v7_fast()
	{
		var perf = MeasureOps(static () => GuidExtensions.CreateVersion7());
		await Assert.That(perf).IsGreaterThan(1_000_000);
	}

#if NET9_0_OR_GREATER
	[Test]
	[Property("CI", "false")]
	public async Task Should_create_v7_net9()
	{
		var perf = MeasureOps(static () => Guid.CreateVersion7());
		await Assert.That(perf).IsGreaterThan(1_000_000);
	}
#endif

	[Test]
	public async Task Should_create_v7_monotonic()
	{
		Guid prev = GuidExtensions.CreateVersion7();
		//Console.WriteLine(prev);
		bool allGood = true;
		for (int i = 0; i < 1_000_000; i++)
		{
			var newGuid = GuidExtensions.CreateVersion7();
			if (newGuid.ToString()[0] != '0')
			{
				Console.WriteLine("#" + i + " WTF!");
				Console.WriteLine(newGuid);
			}
			//Console.WriteLine(newGuid);
			if (newGuid.CompareTo(prev) <= 0)
			{
				allGood = false;
				Console.WriteLine("#" + i);
				Console.WriteLine(prev);
				Console.WriteLine(newGuid);
			}
			prev = newGuid;
		}
		await Assert.That(allGood).IsEqualTo(true);
	}

	[Test]
	[Arguments(1)]
	[Arguments(6)]
	[Arguments(7)]
	public async Task Should_create_programatic_version_with_adequate_time(int version)
	{
		Guid guid = GuidExtensions.Create(version: version);
		DateTime time = guid.GetTimestamp();
		DateTime now = DateTime.UtcNow;
		await Assert.That((now - time).TotalSeconds).IsLessThan(1);
	}
}
