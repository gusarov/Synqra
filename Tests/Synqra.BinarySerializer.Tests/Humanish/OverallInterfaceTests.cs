using Synqra.Tests.SampleModels.Syncronization;
using System;
using System.Collections.Generic;
using System.Text;

namespace Synqra.BinarySerializer.Tests.Humanish;

internal class OverallInterfaceTests
{
	[Test]
	public async Task Should_10_binary_serialize()
	{
		var sbx = new SbxSerializer();
		Span<byte> bytes = stackalloc byte[1024];
		int pos = 0;
		sbx.Serialize(in bytes, ref pos, new SampleTaskModel { Number = 7, Subject = "Sample 1" });
		// Should now froze and not allow changes to metadata!!
		Assert.Throws<InvalidOperationException>(() =>
		{
			sbx.Map(777, typeof(SampleTaskModel));
		});
	}

	[Test]
	public async Task Should_11_autopopulate_header_metadata()
	{
		// Pre-configured metadata for SampleTaskModel
		var sbx = new SbxSerializer();
		sbx.Map(777, typeof(SampleTaskModel));

		Span<byte> bytes = stackalloc byte[1024];
		int pos = 0;

		sbx.Serialize(in bytes, ref pos, "Test");
		sbx.Serialize(in bytes, ref pos, new SampleTaskModel { Number = 7, Subject = "Sample 1" });
		pos = 0;

		sbx = new SbxSerializer();
		sbx.Map(777, typeof(SampleTaskModel));

	}

	[Test]
	public async Task Should_12_autopopulate_header_metadata()
	{
		// Pre-configured metadata for SampleTaskModel
		var sbx = new SbxSerializer();
		sbx.Map(777, typeof(SampleTaskModel));

		Span<byte> bytes = stackalloc byte[1024];
		int pos = 0;

		sbx.Serialize(in bytes, ref pos, "Test");
		sbx.Serialize(in bytes, ref pos, new SampleTaskModel { Number = 7, Subject = "Sample 1" });
	}

	[Test]
	public async Task Should_13_not_allow_serialization_untill_metadata_configured()
	{
		var sbx = new SbxSerializer();

		var ex = Assert.Throws<InvalidOperationException>(() =>
		{
			Span<byte> bytes = stackalloc byte[1024];
			int pos = 0;
			sbx.Serialize(in bytes, ref pos, "Test");
		});

		await Assert.That(ex.Message).IsEqualTo("No schemas configured.");
	}
}
