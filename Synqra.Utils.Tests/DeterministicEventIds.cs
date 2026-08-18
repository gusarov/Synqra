using System;
using TUnit.Assertions.AssertConditions.Throws;

namespace Synqra.Utils.Tests;

internal class DeterministicEventIds
{
	[Test]
	public async Task Should_be_deterministic()
	{
		var cmd = GuidExtensions.CreateVersion7();
		await Assert.That(SynqraIdDerivation.Derive(cmd, 1)).IsEqualTo(SynqraIdDerivation.Derive(cmd, 1));
		await Assert.That(SynqraIdDerivation.Derive(cmd, 2)).IsEqualTo(SynqraIdDerivation.Derive(cmd, 2));
	}

	[Test]
	public async Task Should_print()
	{
		var cmd = GuidExtensions.CreateVersion7();
		Console.WriteLine(cmd);
		Console.WriteLine(SynqraIdDerivation.Derive(cmd, 1));
	}

	[Test]
	public async Task Should_not_mutate_the_command_id()
	{
		var cmd = GuidExtensions.CreateVersion7();
		var copy = cmd;
		SynqraIdDerivation.Derive(cmd, 5);
		await Assert.That(cmd).IsEqualTo(copy);
	}

	[Test]
	public async Task Should_stay_a_valid_v7_and_keep_the_command_timestamp()
	{
		var cmd = GuidExtensions.CreateVersion7();
		var ev = SynqraIdDerivation.Derive(cmd, 1);
		await Assert.That(ev.GetVersion()).IsEqualTo(7);
		await Assert.That(ev.GetVariant()).IsEqualTo(1);
		await Assert.That(ev.GetTimestamp()).IsEqualTo(cmd.GetTimestamp());
	}

	[Test]
	public async Task Should_sort_after_the_command_and_in_ordinal_order()
	{
		var cmd = GuidExtensions.CreateVersion7();
		var e0 = SynqraIdDerivation.Derive(cmd, 0);
		var e1 = SynqraIdDerivation.Derive(cmd, 1);
		var e2 = SynqraIdDerivation.Derive(cmd, 2);
		await Assert.That(e0).IsEqualTo(cmd); // ordinal 0 == the command id itself
		await Assert.That(e1.CompareTo(e0)).IsEqualTo(1);
		await Assert.That(e2.CompareTo(e1)).IsEqualTo(1);
	}

	[Test]
	public async Task Should_reject_a_negative_ordinal()
	{
		var cmd = GuidExtensions.CreateVersion7();
		await Assert.That(() => { _ = SynqraIdDerivation.Derive(cmd, -1); }).Throws<ArgumentOutOfRangeException>();
	}

	// ---- structured (C0DE v8) allocation mode -------------------------------------------------

	static readonly Guid CommittedPinnedStream   = new("C0DE0000-0000-8000-8005-000000000001");
	static readonly Guid StagingPinnedStream     = new("C0DE0000-0000-8000-9005-000000000001");
	static readonly Guid CommittedGeneratedStream = new("C0DE0000-0000-8000-A005-000000000001");
	static readonly Guid StagingGeneratedStream  = new("C0DE0000-0000-8000-B005-000000000001");

	[Test]
	public async Task Should_split_the_mode_nibble_into_two_orthogonal_bits()
	{
		await Assert.That(CommittedPinnedStream.GetAllocationMode()).IsEqualTo(AllocationMode.CommittedPinned);
		await Assert.That(StagingPinnedStream.GetAllocationMode()).IsEqualTo(AllocationMode.StagingPinned);
		await Assert.That(CommittedGeneratedStream.GetAllocationMode()).IsEqualTo(AllocationMode.CommittedGenerated);
		await Assert.That(StagingGeneratedStream.GetAllocationMode()).IsEqualTo(AllocationMode.StagingGenerated);

		await Assert.That(CommittedPinnedStream.IsCommitted()).IsTrue();
		await Assert.That(CommittedPinnedStream.IsPinned()).IsTrue();
		await Assert.That(StagingPinnedStream.IsStaging()).IsTrue();
		await Assert.That(StagingPinnedStream.IsPinned()).IsTrue();
		await Assert.That(CommittedGeneratedStream.IsCommitted()).IsTrue();
		await Assert.That(CommittedGeneratedStream.IsGenerated()).IsTrue();
		await Assert.That(StagingGeneratedStream.IsStaging()).IsTrue();
		await Assert.That(StagingGeneratedStream.IsGenerated()).IsTrue();
	}

	[Test]
	public async Task Should_read_the_same_semantic_class_under_every_mode()
	{
		foreach (var id in new[] { CommittedPinnedStream, StagingPinnedStream, CommittedGeneratedStream, StagingGeneratedStream })
		{
			await Assert.That(id.GetSemanticFamily()).IsEqualTo((byte)0x0); // 005 is family 0 code 05, not "family 5"
			await Assert.That(id.GetSemanticCode()).IsEqualTo((byte)0x05);
			await Assert.That(id.GetSemanticClass()).IsEqualTo((ushort)0x005);
		}
	}

	[Test]
	public async Task Should_treat_the_generated_bit_as_provenance_only()
	{
		// 8C02 <-> AC02 and 9C17 <-> BC17 are the same semantic allocation, differing only in provenance
		var committedPinned = new Guid("C0DE0000-0000-8000-8C02-000000000000");
		var committedGenerated = new Guid("C0DE0000-0000-8000-AC02-000000000100");
		await Assert.That(committedGenerated.GetSemanticClass()).IsEqualTo(committedPinned.GetSemanticClass());
		await Assert.That(committedGenerated.IsCommitted()).IsEqualTo(committedPinned.IsCommitted());

		var stagingPinned = new Guid("C0DE0000-0000-8000-9C17-000000000000");
		var stagingGenerated = new Guid("C0DE0000-0000-8000-BC17-000000000100");
		await Assert.That(stagingGenerated.GetSemanticClass()).IsEqualTo(stagingPinned.GetSemanticClass());
		await Assert.That(stagingGenerated.IsStaging()).IsEqualTo(stagingPinned.IsStaging());

		// but the staging bit does change the registry: 8C02 and 9C02 are independent allocations
		var otherRegistry = new Guid("C0DE0000-0000-8000-9C02-000000000000");
		await Assert.That(otherRegistry.GetSemanticClass()).IsEqualTo(committedPinned.GetSemanticClass());
		await Assert.That(otherRegistry.IsCommitted()).IsNotEqualTo(committedPinned.IsCommitted());
	}

	// ---- structured (C0DE v8) derivation -------------------------------------------------------

	static readonly Guid StructuredCommand = new("C0DE0000-0000-8000-AC01-000000000100"); // AddComponentCommand instance
	static readonly Guid ComponentAddedType = new("C0DEADD0-1032-8000-8E01-000000000000");
	static readonly Guid CommandCreatedType = new("C0DEADD0-1032-8000-8E0E-000000000000");
	static readonly Guid StagingEventType = new("C0DE0000-0000-8000-9E15-000000000000");

	[Test]
	public async Task Should_recognise_only_structured_ids()
	{
		await Assert.That(StructuredCommand.IsStructuredId()).IsTrue();
		await Assert.That(ComponentAddedType.IsStructuredId()).IsTrue();
		await Assert.That(GuidExtensions.CreateVersion7().IsStructuredId()).IsFalse();
		await Assert.That(Guid.Empty.IsStructuredId()).IsFalse();
		// right version, wrong magic — a v8 hash is not a structured id
		await Assert.That(new Guid("11110000-0000-8000-8001-000000000000").IsStructuredId()).IsFalse();
	}

	[Test]
	public async Task Should_take_the_registry_from_the_event_type_and_set_the_generated_bit()
	{
		// committed event type -> committed+generated instance (A), never the command's own mode
		var ev = SynqraIdDerivation.DeriveEventId(StructuredCommand, ComponentAddedType, 1);
		await Assert.That(ev).IsEqualTo(new Guid("C0DE0000-0000-8000-AE01-000000000101"));

		// staging event type -> staging+generated instance (B)
		var staged = SynqraIdDerivation.DeriveEventId(StructuredCommand, StagingEventType, 1);
		await Assert.That(staged).IsEqualTo(new Guid("C0DE0000-0000-8000-BE15-000000000101"));
	}

	[Test]
	public async Task Should_ignore_the_commands_own_mode()
	{
		// the same node under a staging-pinned command still yields a committed+generated event id,
		// because the registry comes from the event type and the provenance from the derivation
		var stagingPinnedCommand = new Guid("C0DE0000-0000-8000-9C01-000000000100");
		var ev = SynqraIdDerivation.DeriveEventId(stagingPinnedCommand, ComponentAddedType, 1);
		await Assert.That(ev).IsEqualTo(new Guid("C0DE0000-0000-8000-AE01-000000000101"));
	}

	[Test]
	public async Task Should_distinguish_two_event_types_of_one_command()
	{
		var wrapper = SynqraIdDerivation.DeriveEventId(StructuredCommand, CommandCreatedType, 0);
		var domain = SynqraIdDerivation.DeriveEventId(StructuredCommand, ComponentAddedType, 1);
		await Assert.That(wrapper).IsEqualTo(new Guid("C0DE0000-0000-8000-AE0E-000000000100"));
		await Assert.That(domain).IsEqualTo(new Guid("C0DE0000-0000-8000-AE01-000000000101"));
		await Assert.That(wrapper).IsNotEqualTo(domain);
		// the ordinal-0 wrapper no longer aliases the command id: it names its own event type
		await Assert.That(wrapper).IsNotEqualTo(StructuredCommand);
	}

	[Test]
	public async Task Should_keep_two_events_of_the_same_type_apart_by_ordinal()
	{
		var first = SynqraIdDerivation.DeriveEventId(StructuredCommand, ComponentAddedType, 1);
		var second = SynqraIdDerivation.DeriveEventId(StructuredCommand, ComponentAddedType, 2);
		await Assert.That(second.CompareTo(first)).IsEqualTo(1);
		await Assert.That(second).IsEqualTo(new Guid("C0DE0000-0000-8000-AE01-000000000102"));
	}

	[Test]
	public async Task Should_fall_back_to_plain_derivation_for_opaque_command_ids()
	{
		var v7 = GuidExtensions.CreateVersion7();
		// production: the command id is opaque, so there is no class to carry and nothing changes
		await Assert.That(SynqraIdDerivation.DeriveEventId(v7, ComponentAddedType, 1)).IsEqualTo(SynqraIdDerivation.Derive(v7, 1));
		// an opaque event type is fine too, as long as the command id is opaque as well
		var derivedTypeId = GuidExtensions.CreateVersion5(new Guid("C0DEADD0-1032-8000-8000-000000000001"), "Some.Consumer.Event");
		await Assert.That(SynqraIdDerivation.DeriveEventId(v7, derivedTypeId, 1)).IsEqualTo(SynqraIdDerivation.Derive(v7, 1));
	}

	[Test]
	public async Task Should_refuse_to_invent_a_semantic_class_for_an_unstructured_event_type()
	{
		// falling back here would leave the command's own Cnn in the event id — a false semantic claim
		var derivedTypeId = GuidExtensions.CreateVersion5(new Guid("C0DEADD0-1032-8000-8000-000000000001"), "Some.Consumer.Event");
		await Assert.That(() => { _ = SynqraIdDerivation.DeriveEventId(StructuredCommand, derivedTypeId, 1); })
			.Throws<ArgumentException>();
	}

	[Test]
	public async Task Should_not_mutate_its_inputs()
	{
		var cmd = StructuredCommand;
		var type = ComponentAddedType;
		SynqraIdDerivation.DeriveEventId(cmd, type, 3);
		await Assert.That(cmd).IsEqualTo(StructuredCommand);
		await Assert.That(type).IsEqualTo(ComponentAddedType);
	}

	[Test]
	public async Task Should_reject_a_negative_ordinal_when_structured()
	{
		await Assert.That(() => { _ = SynqraIdDerivation.DeriveEventId(StructuredCommand, ComponentAddedType, -1); })
			.Throws<ArgumentOutOfRangeException>();
	}

	[Test]
	public async Task Should_refuse_to_let_the_node_overflow_into_the_class()
	{
		var atTheBrim = new Guid("C0DE0000-0000-8000-AC01-FFFFFFFFFFFF");
		await Assert.That(() => { _ = SynqraIdDerivation.DeriveEventId(atTheBrim, ComponentAddedType, 1); })
			.Throws<ArgumentOutOfRangeException>();
	}
}