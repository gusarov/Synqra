using System;
using TUnit.Assertions.AssertConditions.Throws;

namespace Synqra.Utils.Tests;

// model.md §8.9 "Test ids": test ids sort by creation first, class second.
internal class DeterministicIdOrder
{
	static readonly Guid AddComponentCommandType = new("C0DEADD0-1032-8000-8C01-000000000000");
	static readonly Guid StagingCommandType = new("C0DE0000-0000-8000-9C45-000000000000");
	static readonly Guid CommandCreatedType = new("C0DEADD0-1032-8000-8E0E-000000000000");
	static readonly Guid ComponentAddedType = new("C0DEADD0-1032-8000-8E01-000000000000");

	[Test]
	public async Task Should_sort_ids_of_different_classes_in_creation_order()
	{
		var ids = new DeterministicSynqraIdProvider();
		// classes deliberately descend (C01, 005, 001) so a class-first layout would sort them backwards
		var minted = new[]
		{
			ids.CreateCommandId(AddComponentCommandType),
			ids.CreateStreamId(),
			ids.CreateComponentId(),
			ids.CreateLinkId(),
			ids.CreateCommandId(StagingCommandType),
			ids.CreateComponentId(),
			ids.CreateStreamId(),
		};
		for (var i = 1; i < minted.Length; i++)
		{
			await Assert.That(minted[i - 1].CompareBigEndian(minted[i])).IsLessThan(0);
			await Assert.That(minted[i - 1].CompareTo(minted[i])).IsLessThan(0);
		}
	}

	[Test]
	public async Task Should_read_as_a_test_id_numbered_in_its_leading_groups()
	{
		var ids = new DeterministicSynqraIdProvider();
		var stream = ids.CreateStreamId();
		var command = ids.CreateCommandId(AddComponentCommandType);
		await Assert.That(stream).IsEqualTo(new Guid("C0DE0000-0000-8001-A005-000000000001"));
		await Assert.That(command).IsEqualTo(new Guid("C0DE0000-0000-8002-AC01-000000000100"));
	}

	[Test]
	public async Task Should_sort_derived_events_right_after_their_command_and_before_the_next_id()
	{
		var ids = new DeterministicSynqraIdProvider();
		var command = ids.CreateCommandId(AddComponentCommandType);
		var wrapper = SynqraIdDerivation.DeriveEventId(command, CommandCreatedType, 0);
		var added = SynqraIdDerivation.DeriveEventId(command, ComponentAddedType, 1);
		var next = ids.CreateComponentId();
		var nextCommand = ids.CreateCommandId(AddComponentCommandType);
		foreach (var ev in new[] { wrapper, added })
		{
			await Assert.That(command.CompareBigEndian(ev)).IsLessThan(0);
			await Assert.That(ev.CompareBigEndian(next)).IsLessThan(0);
			await Assert.That(ev.CompareBigEndian(nextCommand)).IsLessThan(0);
		}
		await Assert.That(wrapper).IsEqualTo(new Guid("C0DE0000-0000-8001-AE0E-000000000100"));
		await Assert.That(added).IsEqualTo(new Guid("C0DE0000-0000-8001-AE01-000000000101"));
	}

	[Test]
	public async Task Should_keep_mode_and_class_readable()
	{
		var ids = new DeterministicSynqraIdProvider();
		var committedCommand = ids.CreateCommandId(AddComponentCommandType);
		var stagingCommand = ids.CreateCommandId(StagingCommandType);
		var stream = ids.CreateStreamId();
		var component = ids.CreateComponentId();
		var link = ids.CreateLinkId();
		foreach (var id in new[] { committedCommand, stagingCommand, stream, component, link })
		{
			await Assert.That(id.IsStructuredId()).IsTrue();
			await Assert.That(id.IsGenerated()).IsTrue();
		}
		await Assert.That(committedCommand.GetAllocationMode()).IsEqualTo(AllocationMode.CommittedGenerated);
		await Assert.That(committedCommand.GetSemanticClass()).IsEqualTo((ushort)0xC01);
		await Assert.That(stagingCommand.GetAllocationMode()).IsEqualTo(AllocationMode.StagingGenerated);
		await Assert.That(stagingCommand.GetSemanticClass()).IsEqualTo((ushort)0xC45);
		await Assert.That(stream.GetAllocationMode()).IsEqualTo(AllocationMode.CommittedGenerated);
		await Assert.That(stream.GetSemanticClass()).IsEqualTo((ushort)0x005);
		await Assert.That(component.GetSemanticClass()).IsEqualTo((ushort)0x001);
		await Assert.That(link.GetSemanticClass()).IsEqualTo((ushort)0x003);
	}

	[Test]
	public async Task Should_sort_after_every_hand_written_fixture()
	{
		var ids = new DeterministicSynqraIdProvider();
		// the all-zero internal prefix of hand-written fixtures can never be minted
		var fixture = new Guid("C0DE0000-0000-8000-AC01-FFFFFFFFFF00");
		await Assert.That(fixture.CompareBigEndian(ids.CreateCommandId(AddComponentCommandType))).IsLessThan(0);
	}

	[Test]
	public async Task Should_count_independently_per_provider()
	{
		// two providers (two hosts in one test) need not agree: each counts from 1, so their ids may
		// coincide and have no order between them — only one provider's ids are in creation order
		var client = new DeterministicSynqraIdProvider();
		var server = new DeterministicSynqraIdProvider();
		var clientFirst = client.CreateComponentId();
		client.CreateComponentId();
		var serverFirst = server.CreateComponentId();
		await Assert.That(serverFirst).IsEqualTo(clientFirst);
	}

	[Test]
	public async Task Should_carry_each_group_in_byte_order()
	{
		// 0xFFF -> 0x1000 crosses group 3 into group 2, 0xFFFFFFF -> 0x10000000 crosses group 2 into group 1
		foreach (var (before, after) in new[] { (0xFFFL, "C0DE0000-0001-8000"), (0xFFFFFFFL, "C0DE0001-0000-8000") })
		{
			var ids = new DeterministicSynqraIdProvider(before - 1);
			var first = ids.CreateComponentId();
			var second = ids.CreateComponentId();
			await Assert.That(second.ToString("D").ToUpperInvariant()).StartsWith(after);
			await Assert.That(first.CompareBigEndian(second)).IsLessThan(0);
		}
	}

	[Test]
	public async Task Should_carry_past_the_magic_once_the_test_runs_deep()
	{
		// 2^44 - 1 ids still read C0DE; the next carries into the magic: no longer a structured id, still in creation order
		var ids = new DeterministicSynqraIdProvider((1L << 44) - 2);
		var lastStructured = ids.CreateComponentId();
		var firstBeyond = ids.CreateComponentId();
		await Assert.That(lastStructured).IsEqualTo(new Guid("C0DEFFFF-FFFF-8FFF-A001-000000000001"));
		await Assert.That(firstBeyond).IsEqualTo(new Guid("C0DF0000-0000-8000-A001-000000000001"));
		await Assert.That(lastStructured.IsStructuredId()).IsTrue();
		await Assert.That(firstBeyond.IsStructuredId()).IsFalse();
		await Assert.That(lastStructured.CompareBigEndian(firstBeyond)).IsLessThan(0);
		await Assert.That(firstBeyond.GetVersion()).IsEqualTo(8);
	}

	[Test]
	public async Task Should_refuse_to_wrap_past_the_last_creation_number()
	{
		// FFFFFFFF-FFFF-8FFF is the last of (2^60 - 1) - (0xC0DE << 44) ~ 2.8e17 creation numbers
		var ids = new DeterministicSynqraIdProvider((1L << 60) - 1 - (0xC0DEL << 44) - 1);
		await Assert.That(ids.CreateComponentId()).IsEqualTo(new Guid("FFFFFFFF-FFFF-8FFF-A001-000000000001"));
		await Assert.That(() => { _ = ids.CreateComponentId(); }).Throws<InvalidOperationException>();
	}
}
