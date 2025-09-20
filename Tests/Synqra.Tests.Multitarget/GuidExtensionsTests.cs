using Synqra.Tests.TestHelpers;
using System.Runtime.InteropServices;

namespace Synqra.Tests.Multitarget;

public class GuidExtensionsTests3 : BaseTest
{
	[SetUp]
	public void Setup()
	{
	}

	// Test vectors from RFC 4122 and RFC 9562
	// https://www.rfc-editor.org/rfc/rfc9562.html#name-test-vectors
	[Test]
	[TestCase("C232AB00-9414-11EC-B3C8-9F6BDECED846", 1, 1)]
	[TestCase("5df41881-3aed-3515-88a7-2f4a814cf09e", 1, 3)]
	[TestCase("919108f7-52d1-4320-9bac-f847db4148a8", 1, 4)]
	[TestCase("2ed6657d-e927-568b-95e1-2665a8aea6a2", 1, 5)]
	[TestCase("1EC9414C-232A-6B00-B3C8-9F6BDECED846", 1, 6)]
	[TestCase("017F22E2-79B0-7CC3-98C4-DC0C0C07398F", 1, 7)]
	public void Should_detect_test_vectors(string testVector, int expectedVariant, int expectedVersion)
	{
		var guid = new Guid(testVector);
		Assert.That(guid.GetVariant(), Is.EqualTo(expectedVariant));
		Assert.That(guid.GetVersion(), Is.EqualTo(expectedVersion));
	}

	[Test]
	public void Should_handle_v1_test_vector()
	{
		var guid = new Guid("C232AB00-9414-11EC-B3C8-9F6BDECED846");
		Assert.That(guid.GetVariant(), Is.EqualTo(1));
		Assert.That(guid.GetVersion(), Is.EqualTo(1));

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		Assert.That(timestamp, Is.EqualTo(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)).ToUniversalTime().DateTime));
	}

	[Test]
	[Obsolete]
	public void Should_handle_v1_test_vector_create()
	{
		var guid = GuidExtensions.CreateVersion1(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)), clockSeq: 0x33C8, node: 0x9F6BDECED846);
		Assert.That(guid, Is.EqualTo(new Guid("C232AB00-9414-11EC-B3C8-9F6BDECED846")));
	}

	[Test]
	public void Should_handle_namespace_id_starting_vector()
	{
		var guid = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"); // see https://www.rfc-editor.org/rfc/rfc9562.html?utm_source=chatgpt.com#section-6.5
		Assert.That(guid.GetVariant(), Is.EqualTo(1));
		Assert.That(guid.GetVersion(), Is.EqualTo(1));

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp); // 1998-02-04 22:13:53 (likely the date when Guid v3 was developed)
	}

	[Test]
	public void Should_handle_v3_test_vector()
	{
		var guid = GuidExtensions.CreateVersion3Dns("www.example.com");
		Assert.That(guid.GetVariant(), Is.EqualTo(1));
		Assert.That(guid.GetVersion(), Is.EqualTo(3));

		var expected = new Guid("5df41881-3aed-3515-88a7-2f4a814cf09e");
		Assert.That(guid, Is.EqualTo(expected));
	}

	[Test]
	public void Should_handle_v5_test_vector()
	{
		var guid = GuidExtensions.CreateVersion5Dns("www.example.com");
		Assert.That(guid.GetVariant(), Is.EqualTo(1));
		Assert.That(guid.GetVersion(), Is.EqualTo(5));

		var expected = new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2");
		Assert.That(guid, Is.EqualTo(expected));
	}

	[Test]
	public void Should_handle_v5_custom_online_vector()
	{
		var guid = GuidExtensions.CreateVersion5(new Guid("39771042-7f7c-40bf-bc79-c28d75f826ab"), "abc");

		Assert.That(guid, Is.EqualTo(new Guid("c5c35eef-366a-510c-a735-1ffd99bc4304")));
	}

	[Test]
	public void Should_handle_v6_test_vector()
	{
		var guid = new Guid("1EC9414C-232A-6B00-B3C8-9F6BDECED846");
		Assert.That(guid.GetVariant(), Is.EqualTo(1));
		Assert.That(guid.GetVersion(), Is.EqualTo(6));

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		Assert.That(timestamp, Is.EqualTo(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)).ToUniversalTime().DateTime));
	}

	[Test]
	[Obsolete]
	public void Should_handle_v6_test_vector_create()
	{
		var guid = GuidExtensions.CreateVersion6(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)), 0x33C8, 0x9F6BDECED846);
		Assert.That(guid, Is.EqualTo(new Guid("1EC9414C-232A-6B00-B3C8-9F6BDECED846")));
	}

	[Test]
	public void Should_handle_v7_test_vector()
	{
		var guid = new Guid("017F22E2-79B0-7CC3-98C4-DC0C0C07398F");
		Assert.That(guid.GetVariant(), Is.EqualTo(1));
		Assert.That(guid.GetVersion(), Is.EqualTo(7));

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		Assert.That(timestamp, Is.EqualTo(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)).ToUniversalTime().DateTime));
	}

	[Test]
	public void Should_create_v7()
	{
		var guid = GuidExtensions.CreateVersion7(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)));
		Console.WriteLine(guid);
		Assert.That(guid.GetVariant(), Is.EqualTo(1));
		Assert.That(guid.GetVersion(), Is.EqualTo(7));

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		Assert.That(timestamp, Is.EqualTo(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)).ToUniversalTime().DateTime));
	}

	[Test]
	public void Should_handle_v8_example_vector()
	{
		var guid = GuidExtensions.CreateVersion8_Sha256_Dns("www.example.com");
		Assert.That(guid.GetVariant(), Is.EqualTo(1));
		Assert.That(guid.GetVersion(), Is.EqualTo(8));

		var expected = new Guid("5c146b14-3c52-8afd-938a-375d0df1fbf6");
		Assert.That(guid, Is.EqualTo(expected));
	}

	[Test]
	public void Should_compare_guids()
	{
		Span<byte> arr = stackalloc byte[16];
		for (byte i = 0; i < 16; i++)
		{
			arr[i] = i;
		}
		Guid prev = FromNetworkOrder(arr);
		Console.WriteLine(prev);
		Assert.AreEqual(prev, new Guid("00010203-0405-0607-0809-0a0b0c0d0e0f"));
		for (int i = 0; i < 16; i++)
		{
			arr[i]++;
			Guid newGuid = FromNetworkOrder(arr);
			Console.WriteLine(newGuid);
			Assert.IsTrue(newGuid.CompareTo(prev) > 0, "GUIDs are not sequential");
			Assert.IsTrue(prev.CompareTo(newGuid) < 0, "GUIDs are not sequential");
			prev = newGuid;
		}
	}

	Guid FromNetworkOrder([In] ReadOnlySpan<byte> span)
	{
		var arr = span.ToArray();
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(arr, 0, 4);
			Array.Reverse(arr, 4, 2);
			Array.Reverse(arr, 6, 2);
		}
		return new Guid(arr);
	}

#if NET9_0_OR_GREATER
	[Test]
	public void Should_create_v7_near_net()
	{
		Console.WriteLine(GuidExtensions.CreateVersion7());
		Console.WriteLine(Guid.CreateVersion7());
	}
#endif

	[Test]
	public void Should_create_v7_same_ms_sequence()
	{
		Guid prev = GuidExtensions.CreateVersion7();
		Console.WriteLine(prev);
		for (int i = 0; i < 24; i++)
		{
			var newGuid = GuidExtensions.CreateVersion7();
			Console.WriteLine(newGuid);
			Assert.IsTrue(newGuid.CompareTo(prev) > 0, "GUIDs are not sequential");
			prev = newGuid;
		}
	}

	[Test]
	public void Should_create_v7_manual_time_sequence()
	{
		var now = new DateTime(638939296991929998, DateTimeKind.Utc);
		Guid prev = GuidExtensions.CreateVersion7(now);
		Console.WriteLine(prev);
		for (int i = 0; i < 24; i++)
		{
			var newGuid = GuidExtensions.CreateVersion7(now);
			Console.WriteLine(newGuid);
			Assert.IsTrue(newGuid.CompareTo(prev) > 0, "GUIDs are not sequential");
			prev = newGuid;
		}
	}

	[Test]
	public void Should_create_v7_fast()
	{
		var perf = MeasureOps(() => GuidExtensions.CreateVersion7());
		Assert.IsTrue(perf > 100_000, "Too slow");
	}

#if NET9_0_OR_GREATER
	[Test]
	public void Should_create_v7_net9()
	{
		var perf = MeasureOps(() => Guid.CreateVersion7());
		Assert.IsTrue(perf > 100_000, "Too slow");
	}
#endif

	[Test]
	public void Should_create_v7_monotonic()
	{
		Guid prev = GuidExtensions.CreateVersion7();
		// Console.WriteLine(prev);
		bool allGood = true;
		for (int i = 0; i < 1_000_000; i++)
		{
			var newGuid = GuidExtensions.CreateVersion7();
			// Console.WriteLine(newGuid);
			if (newGuid.CompareTo(prev) <= 0)
			{
				allGood = false;
				Console.WriteLine("#" + i);
				Console.WriteLine(prev);
				Console.WriteLine(newGuid);
			}
			prev = newGuid;
		}
		Assert.IsTrue(allGood, "GUIDs are not sequential");
	}
}
