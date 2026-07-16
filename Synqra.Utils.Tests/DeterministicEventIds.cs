using System;

namespace Synqra.Utils.Tests;

internal class DeterministicEventIds
{
	[Test]
	public async Task Should_be_deterministic()
	{
		var cmd = GuidExtensions.CreateVersion7();
		await Assert.That(GuidExtensions.Derive(cmd, 1)).IsEqualTo(GuidExtensions.Derive(cmd, 1));
		await Assert.That(GuidExtensions.Derive(cmd, 2)).IsEqualTo(GuidExtensions.Derive(cmd, 2));
	}

	[Test]
	public async Task Should_not_mutate_the_command_id()
	{
		var cmd = GuidExtensions.CreateVersion7();
		var copy = cmd;
		GuidExtensions.Derive(cmd, 5);
		await Assert.That(cmd).IsEqualTo(copy);
	}

	[Test]
	public async Task Should_stay_a_valid_v7_and_keep_the_command_timestamp()
	{
		var cmd = GuidExtensions.CreateVersion7();
		var ev = GuidExtensions.Derive(cmd, 1);
		await Assert.That(ev.GetVersion()).IsEqualTo(7);
		await Assert.That(ev.GetVariant()).IsEqualTo(1);
		await Assert.That(ev.GetTimestamp()).IsEqualTo(cmd.GetTimestamp());
	}

	[Test]
	public async Task Should_sort_after_the_command_and_in_ordinal_order()
	{
		var cmd = GuidExtensions.CreateVersion7();
		var e0 = GuidExtensions.Derive(cmd, 0);
		var e1 = GuidExtensions.Derive(cmd, 1);
		var e2 = GuidExtensions.Derive(cmd, 2);
		await Assert.That(e0).IsEqualTo(cmd); // ordinal 0 == the command id itself
		await Assert.That(e1.CompareTo(e0)).IsEqualTo(1);
		await Assert.That(e2.CompareTo(e1)).IsEqualTo(1);
	}

	[Test]
	public async Task Should_reject_a_negative_ordinal()
	{
		var cmd = GuidExtensions.CreateVersion7();
		await Assert.That(() => { _ = GuidExtensions.Derive(cmd, -1); }).Throws<ArgumentOutOfRangeException>();
	}
}
