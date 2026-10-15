using Synqra.BinarySerializer.Tests.Dynamic;

namespace Synqra.BinarySerializer.Tests;

// Types with no SBX Map entry round-trip by type name + field names.
public class BinarySerializationDynamicTypeTests : SbxBaseTest
{
	readonly byte[] _buffer = new byte[10240];

	(Event Result, int Written, int Read) RoundTrip(Event evt)
	{
		var ser = new SbxSerializer();
		var pos = 0;
		ser.Serialize<Event>(_buffer, ref pos, evt);
		HexDump(_buffer.AsSpan(0, pos));

		var deser = new SbxSerializer();
		var depos = 0;
		var result = deser.Deserialize<Event>(_buffer.AsSpan(0, pos), ref depos);
		return (result, pos, depos);
	}

	[Test]
	public async Task Should_10_round_trip_unmapped_bindable_model_nested_in_mapped_type()
	{
		var evt = new CommandCreatedEvent
		{
			EventId = new Guid("0199eeb4-33df-7430-a02a-d9fd1bf80001"),
			CommandId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0002"),
			Data = new DynamicLeafCommand
			{
				CommandId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0002"),
				StreamId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0003"),
				TargetId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0004"),
				Note = "hello",
			},
		};

		var (result, written, read) = RoundTrip(evt);
		await Assert.That(read).IsEqualTo(written);
		var back = (CommandCreatedEvent)result;

		await Assert.That(back.EventId).IsEqualTo(evt.EventId);
		await Assert.That(back.CommandId).IsEqualTo(evt.CommandId);
		var leaf = back.Data as DynamicLeafCommand;
		await Assert.That(leaf).IsNotNull();
		await Assert.That(leaf!.CommandId).IsEqualTo(evt.Data.CommandId);
		await Assert.That(leaf.StreamId).IsEqualTo(evt.Data.StreamId);
		await Assert.That(leaf.TargetId).IsEqualTo(new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0004"));
		await Assert.That(leaf.Note).IsEqualTo("hello");
	}

	[Test]
	public async Task Should_20_round_trip_poco_list_inside_unmapped_bindable_model()
	{
		var expectation = new DynamicExpectation
		{
			TargetId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0011"),
			TargetTypeId = new Guid("C0DE0000-0000-8000-9047-000000000000"),
			ExpectedLastEventId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0012"),
		};
		var evt = new CommandCreatedEvent
		{
			EventId = new Guid("0199eeb4-33df-7430-a02a-d9fd1bf80021"),
			CommandId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0022"),
			Data = new DynamicBatchCommand
			{
				CommandId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0022"),
				Expectations = [expectation],
			},
		};

		var (result, written, read) = RoundTrip(evt);
		await Assert.That(read).IsEqualTo(written);
		var back = (CommandCreatedEvent)result;

		var batch = back.Data as DynamicBatchCommand;
		await Assert.That(batch).IsNotNull();
		await Assert.That(batch!.Commands.Count).IsEqualTo(0);
		await Assert.That(batch.Expectations.Count).IsEqualTo(1);
		await Assert.That(batch.Expectations[0].TargetId).IsEqualTo(expectation.TargetId);
		await Assert.That(batch.Expectations[0].TargetTypeId).IsEqualTo(expectation.TargetTypeId);
		await Assert.That(batch.Expectations[0].ExpectedLastEventId).IsEqualTo(expectation.ExpectedLastEventId);
	}

	[Test]
	public async Task Should_30_round_trip_command_created_event_with_batch_command()
	{
		var add = new AddComponentCommand
		{
			CommandId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0031"),
			StreamId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0030"),
			TargetTypeId = new Guid("C0DE0000-0000-8000-9047-000000000000"),
			TargetId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0033"),
			ComponentTypeId = new Guid("C0DE0000-0000-8000-9048-000000000000"),
			ComponentId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0034"),
		};
		var delete = new DeleteComponentCommand
		{
			CommandId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0035"),
			StreamId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0030"),
			TargetTypeId = new Guid("C0DE0000-0000-8000-9047-000000000000"),
			TargetId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0033"),
			ComponentTypeId = new Guid("C0DE0000-0000-8000-9049-000000000000"),
			ComponentId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0036"),
		};
		var expectation = new DynamicExpectation
		{
			TargetId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0033"),
			TargetTypeId = new Guid("C0DE0000-0000-8000-9047-000000000000"),
			ExpectedLastEventId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0037"),
		};
		var evt = new CommandCreatedEvent
		{
			EventId = new Guid("0199eeb4-33df-7430-a02a-d9fd1bf80038"),
			CommandId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0039"),
			Data = new DynamicBatchCommand
			{
				CommandId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0039"),
				StreamId = new Guid("0199eeb4-33dc-78b2-b1c5-ab90b83b0030"),
				Commands = [add, delete],
				Expectations = [expectation],
			},
		};

		var (result, written, read) = RoundTrip(evt);
		await Assert.That(read).IsEqualTo(written);
		var back = (CommandCreatedEvent)result;

		await Assert.That(back.EventId).IsEqualTo(evt.EventId);
		await Assert.That(back.CommandId).IsEqualTo(evt.CommandId);
		var batch = back.Data as DynamicBatchCommand;
		await Assert.That(batch).IsNotNull();
		await Assert.That(batch!.CommandId).IsEqualTo(evt.Data.CommandId);
		await Assert.That(batch.StreamId).IsEqualTo(evt.Data.StreamId);
		await Assert.That(batch.Commands.Count).IsEqualTo(2);

		var add2 = batch.Commands[0] as AddComponentCommand;
		await Assert.That(add2).IsNotNull();
		await Assert.That(add2!.CommandId).IsEqualTo(add.CommandId);
		await Assert.That(add2.StreamId).IsEqualTo(add.StreamId);
		await Assert.That(add2.TargetTypeId).IsEqualTo(add.TargetTypeId);
		await Assert.That(add2.TargetId).IsEqualTo(add.TargetId);
		await Assert.That(add2.ComponentTypeId).IsEqualTo(add.ComponentTypeId);
		await Assert.That(add2.ComponentId).IsEqualTo(add.ComponentId);

		var delete2 = batch.Commands[1] as DeleteComponentCommand;
		await Assert.That(delete2).IsNotNull();
		await Assert.That(delete2!.CommandId).IsEqualTo(delete.CommandId);
		await Assert.That(delete2.TargetId).IsEqualTo(delete.TargetId);
		await Assert.That(delete2.ComponentTypeId).IsEqualTo(delete.ComponentTypeId);
		await Assert.That(delete2.ComponentId).IsEqualTo(delete.ComponentId);

		await Assert.That(batch.Expectations.Count).IsEqualTo(1);
		await Assert.That(batch.Expectations[0].TargetId).IsEqualTo(expectation.TargetId);
		await Assert.That(batch.Expectations[0].TargetTypeId).IsEqualTo(expectation.TargetTypeId);
		await Assert.That(batch.Expectations[0].ExpectedLastEventId).IsEqualTo(expectation.ExpectedLastEventId);
	}
}
