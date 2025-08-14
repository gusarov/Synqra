using Synqra.Tests.TestHelpers;
using System.Diagnostics;

namespace Synqra.Tests.Miscellaneous;

[NotInParallel]
public class GuidExtensionsTests
{
	[Test]
	public async Task Should_01_Create_Version5_Guid()
	{
		var guid1 = GuidExtensions.CreateVersion5("Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsNotEqualTo(default);
	}

	[Test]
	public async Task Should_01_Create_Version3_Guid()
	{
		var guid1 = GuidExtensions.CreateVersion3("Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsNotEqualTo(default);
	}

	[Test]
	public async Task Should_02_Create_Version5_Guid2()
	{
		var guid1 = GuidExtensions.CreateVersion5("Test");
		var guid2 = GuidExtensions.CreateVersion5("Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsEqualTo(guid2);
		await Assert.That(guid1).IsNotEqualTo(GuidExtensions.CreateVersion3("Test"));
	}

	[Test]
	public async Task Should_02_Create_Version3_Guid2()
	{
		var guid1 = GuidExtensions.CreateVersion3("Test");
		var guid2 = GuidExtensions.CreateVersion3("Test");
		Trace.WriteLine(guid1);
		await Assert.That(guid1).IsEqualTo(guid2);
		await Assert.That(guid1).IsNotEqualTo(GuidExtensions.CreateVersion5("Test"));
	}

	[Test]
	[Skip("performance")]
	[Explicit]
	public async Task Should_create_v5_Guid_quickly()
	{
		await Assert.That(PerformanceTestUtils.MeasureOps(async () =>
		{
			GuidExtensions.CreateVersion5("Test");
		})).IsGreaterThan(500_000);
	}

	[Test]
	[Skip("performance")]
	[Explicit]
	public async Task Should_create_v3_Guid_quickly()
	{
		await Assert.That(PerformanceTestUtils.MeasureOps(async () =>
		{
			GuidExtensions.CreateVersion3("Test");
		})).IsGreaterThan(500_000);
	}

	[Test]
	[Skip("performance")]
	[Explicit]
	public async Task Should_create_v5_Guid_random_quickly()
	{
		var buf = new byte[16];
		await Assert.That(PerformanceTestUtils.MeasureOps(async () =>
		{
			Random.Shared.NextBytes(buf);
			GuidExtensions.CreateVersion5(buf);
		})).IsGreaterThan(300_000);
	}

	[Test]
	[Skip("performance")]
	[Explicit]
	public async Task Should_create_v3_Guid_random_quickly()
	{
		var buf = new byte[16];
		await Assert.That(PerformanceTestUtils.MeasureOps(async () =>
		{
			Random.Shared.NextBytes(buf);
			GuidExtensions.CreateVersion3(buf);
		})).IsGreaterThan(300_000);
	}

	[Test]
	[Skip("performance")]
	[Explicit]
	public async Task Should_create_v5_Guid_super_long()
	{
		var buf = new byte[16 * 1024];
		Random.Shared.NextBytes(buf);
		var perf = PerformanceTestUtils.MeasurePerformance(async () =>
		{
			GuidExtensions.CreateVersion5(buf);
		});
		await Assert.That(perf.OperationsPerSecond).IsGreaterThan(10_000);
	}

	[Test]
	[Skip("performance")]
	[Explicit]
	public async Task Should_create_v3_Guid_super_long()
	{
		var buf = new byte[16 * 1024];
		Random.Shared.NextBytes(buf);
		var perf = PerformanceTestUtils.MeasurePerformance(async () =>
		{
			GuidExtensions.CreateVersion3(buf);
		});
		await Assert.That(perf.OperationsPerSecond).IsGreaterThan(10_000);
	}
}
