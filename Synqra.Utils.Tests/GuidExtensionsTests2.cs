using Synqra.Tests.TestHelpers;
using System.Diagnostics;

namespace Synqra.Utils.Tests;

[NotInParallel]
[Category("Performance")]
public class GuidExtensionsTests2Performance : BaseTest
{
	static Guid _namespaceId = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"); // DNS namespace

	[Test]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_create_v5_Guid_quickly()
	{
		await Assert.That(MeasureOps(static async () =>
		{
			GuidExtensions.CreateVersion5(_namespaceId, "Test");
		})).IsGreaterThan(500_000);
	}

	[Test]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_create_v3_Guid_quickly()
	{
		await Assert.That(MeasureOps(static async () =>
		{
			GuidExtensions.CreateVersion3(_namespaceId, "Test");
		})).IsGreaterThan(500_000);
	}

	[Test]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_create_v5_Guid_random_quickly()
	{
		var buf = new byte[16];
		await Assert.That(MeasureOps(async () =>
		{
			RandomShared.NextBytes(buf);
			GuidExtensions.CreateVersion5(_namespaceId, buf);
		})).IsGreaterThan(300_000);
	}

	[Test]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_create_v3_Guid_random_quickly()
	{
		var buf = new byte[16];
		await Assert.That(MeasureOps(async () =>
		{
			RandomShared.NextBytes(buf);
			GuidExtensions.CreateVersion3(_namespaceId, buf);
		})).IsGreaterThan(300_000);
	}

	[Test]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_create_v5_Guid_super_long()
	{
		var buf = new byte[16 * 1024];
		RandomShared.NextBytes(buf);
		var perf = MeasurePerformance(async () =>
		{
			GuidExtensions.CreateVersion5(_namespaceId, buf);
		});
		await Assert.That(perf.OperationsPerSecond).IsGreaterThan(10_000);
	}

	[Test]
	[Category("Performance")]
	[Property("CI", "false")]
	public async Task Should_create_v3_Guid_super_long()
	{
		var buf = new byte[16 * 1024];
		RandomShared.NextBytes(buf);
		var perf = MeasurePerformance(async () =>
		{
			GuidExtensions.CreateVersion3(_namespaceId, buf);
		});
		await Assert.That(perf.OperationsPerSecond).IsGreaterThan(10_000);
	}
}

public class GuidExtensionsTests2 : BaseTest
{
	Guid _namespaceId = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"); // DNS namespace

	[Test]
	public async Task Should_01_Create_Version5_Guid()
	{
		var guid1 = Synqra.GuidExtensions.CreateVersion5(_namespaceId, "Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsNotEqualTo(default);
	}

	[Test]
	public async Task Should_01_Create_Version3_Guid()
	{
		var guid1 = Synqra.GuidExtensions.CreateVersion3(_namespaceId, "Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsNotEqualTo(default);
	}

	[Test]
	public async Task Should_02_Create_Version5_Guid2()
	{
		var guid1 = Synqra.GuidExtensions.CreateVersion5(_namespaceId, "Test");
		var guid2 = Synqra.GuidExtensions.CreateVersion5(_namespaceId, "Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsEqualTo(guid2);
		await Assert.That(guid1).IsNotEqualTo(GuidExtensions.CreateVersion3(_namespaceId, "Test"));
	}

	[Test]
	public async Task Should_02_Create_Version3_Guid2()
	{
		var guid1 = Synqra.GuidExtensions.CreateVersion3(_namespaceId, "Test");
		var guid2 = Synqra.GuidExtensions.CreateVersion3(_namespaceId, "Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsEqualTo(guid2);
		await Assert.That(guid1).IsNotEqualTo(GuidExtensions.CreateVersion5(_namespaceId, "Test"));
	}

}
