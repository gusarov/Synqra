using System;
using TUnit.Assertions.AssertConditions.Throws;

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
	public async Task Should_print()
	{
		var cmd = GuidExtensions.CreateVersion7();
		Console.WriteLine(cmd);
		Console.WriteLine(GuidExtensions.Derive(cmd, 1));
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

	// ---- structured (C0DE v8) derivation -------------------------------------------------------

	static readonly Guid StructuredCommand = new("C0DE0000-0000-8000-9C01-000000000100"); // AddComponentCommand instance
	static readonly Guid ComponentAddedType = new("C0DEADD0-1032-8000-8E01-000000000000");
	static readonly Guid CommandCreatedType = new("C0DEADD0-1032-8000-8E0E-000000000000");

	[Test]
	public async Task Should_recognise_only_structured_ids()
	{
		await Assert.That(StructuredCommand.IsStructuredId()).IsTrue();
		await Assert.That(ComponentAddedType.IsStructuredId()).IsTrue();
		await Assert.That(GuidExtensions.CreateVersion7().IsStructuredId()).IsFalse();
		await Assert.That(Guid.Empty.IsStructuredId()).IsFalse();
		// right version, wrong magic — a v8 hash is not a structured id
		await Assert.That(new Guid("11110000-0000-8000-8F01-000000000000").IsStructuredId()).IsFalse();
	}

	[Test]
	public async Task Should_read_the_stage_and_class_of_a_structured_id()
	{
		await Assert.That(StructuredCommand.GetStructuredStage()).IsEqualTo((byte)0x9);
		await Assert.That(StructuredCommand.GetStructuredClass()).IsEqualTo((ushort)0xC01);
		await Assert.That(ComponentAddedType.GetStructuredStage()).IsEqualTo((byte)0x8);
		await Assert.That(ComponentAddedType.GetStructuredClass()).IsEqualTo((ushort)0xE01);
	}

	[Test]
	public async Task Should_carry_the_event_class_and_the_command_stage()
	{
		var ev = GuidExtensions.DeriveEventId(StructuredCommand, ComponentAddedType, 1);
		await Assert.That(ev).IsEqualTo(new Guid("C0DE0000-0000-8000-9E01-000000000101"));
	}

	[Test]
	public async Task Should_distinguish_two_event_types_of_one_command()
	{
		var wrapper = GuidExtensions.DeriveEventId(StructuredCommand, CommandCreatedType, 0);
		var domain = GuidExtensions.DeriveEventId(StructuredCommand, ComponentAddedType, 1);
		await Assert.That(wrapper).IsEqualTo(new Guid("C0DE0000-0000-8000-9E0E-000000000100"));
		await Assert.That(domain).IsEqualTo(new Guid("C0DE0000-0000-8000-9E01-000000000101"));
		await Assert.That(wrapper).IsNotEqualTo(domain);
		// the ordinal-0 wrapper no longer aliases the command id: it names its own event type
		await Assert.That(wrapper).IsNotEqualTo(StructuredCommand);
	}

	[Test]
	public async Task Should_keep_two_events_of_the_same_type_apart_by_ordinal()
	{
		var first = GuidExtensions.DeriveEventId(StructuredCommand, ComponentAddedType, 1);
		var second = GuidExtensions.DeriveEventId(StructuredCommand, ComponentAddedType, 2);
		await Assert.That(second.CompareTo(first)).IsEqualTo(1);
		await Assert.That(second).IsEqualTo(new Guid("C0DE0000-0000-8000-9E01-000000000102"));
	}

	[Test]
	public async Task Should_fall_back_to_plain_derivation_for_opaque_ids()
	{
		var v7 = GuidExtensions.CreateVersion7();
		// production: the command id is a v7, so there is no class to swap and nothing changes
		await Assert.That(GuidExtensions.DeriveEventId(v7, ComponentAddedType, 1)).IsEqualTo(GuidExtensions.Derive(v7, 1));
		// a structured command whose event type has no structured id also degrades gracefully
		var derivedTypeId = GuidExtensions.CreateVersion5(new Guid("C0DEADD0-1032-8000-8000-000000000001"), "Some.Consumer.Event");
		await Assert.That(GuidExtensions.DeriveEventId(StructuredCommand, derivedTypeId, 1))
			.IsEqualTo(GuidExtensions.Derive(StructuredCommand, 1));
	}

	[Test]
	public async Task Should_not_mutate_its_inputs()
	{
		var cmd = StructuredCommand;
		var type = ComponentAddedType;
		GuidExtensions.DeriveEventId(cmd, type, 3);
		await Assert.That(cmd).IsEqualTo(StructuredCommand);
		await Assert.That(type).IsEqualTo(ComponentAddedType);
	}

	[Test]
	public async Task Should_reject_a_negative_ordinal_when_structured()
	{
		await Assert.That(() => { _ = GuidExtensions.DeriveEventId(StructuredCommand, ComponentAddedType, -1); })
			.Throws<ArgumentOutOfRangeException>();
	}

	[Test]
	public async Task Should_refuse_to_let_the_node_overflow_into_the_class()
	{
		var atTheBrim = new Guid("C0DE0000-0000-8000-9C01-FFFFFFFFFFFF");
		await Assert.That(() => { _ = GuidExtensions.DeriveEventId(atTheBrim, ComponentAddedType, 1); })
			.Throws<ArgumentOutOfRangeException>();
	}
}
