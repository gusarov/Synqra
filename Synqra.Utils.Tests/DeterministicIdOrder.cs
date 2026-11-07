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
	public async Task Should_read_as_a_test_id_with_the_creation_number_at_both_ends()
	{
		var ids = new DeterministicSynqraIdProvider();
		var stream = ids.CreateStreamId();
		var command = ids.CreateCommandId(AddComponentCommandType);
		await Assert.That(stream).IsEqualTo(new Guid("C0DE0001-0000-8000-A005-000000000001"));
		await Assert.That(command).IsEqualTo(new Guid("C0DE0002-0000-8000-AC01-000000000200"));
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
		await Assert.That(wrapper).IsEqualTo(new Guid("C0DE0001-0000-8000-AE0E-000000000100"));
		await Assert.That(added).IsEqualTo(new Guid("C0DE0001-0000-8000-AE01-000000000101"));
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
	public async Task Should_refuse_to_wrap_the_creation_counter()
	{
		var ids = new DeterministicSynqraIdProvider();
		for (var i = 0; i < 0xFFFF; i++)
		{
			ids.CreateComponentId();
		}
		await Assert.That(() => { _ = ids.CreateComponentId(); }).Throws<InvalidOperationException>();
	}
}
