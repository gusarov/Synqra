namespace Synqra.Tests.Multitarget;

public class GuidExtensionsTests3
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
	public void Should_handle_v7_test_vector()
	{
		var guid = new Guid("017F22E2-79B0-7CC3-98C4-DC0C0C07398F");
		Assert.That(guid.GetVariant(), Is.EqualTo(1));
		Assert.That(guid.GetVersion(), Is.EqualTo(7));

		var timestamp = guid.GetTimestamp();
		Console.WriteLine(timestamp);
		Assert.That(timestamp, Is.EqualTo(new DateTimeOffset(2022, 2, 22, 14, 22, 22, TimeSpan.FromHours(-5)).ToUniversalTime().DateTime));
	}
}
