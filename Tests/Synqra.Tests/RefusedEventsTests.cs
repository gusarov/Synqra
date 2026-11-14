using Synqra.Tests.TestHelpers;

namespace Synqra.Tests;

/// <summary>
/// The undo a client writes for an own event the master refused: the state goes back to what the log holds without it, and
/// only where nothing later decides that state already.
/// </summary>
internal class RefusedEventsTests : BaseTest
{
	readonly Guid _stream = Guid.NewGuid();
	readonly Guid _thing = Guid.NewGuid();
	readonly Guid _facet = Guid.NewGuid();

	[Test]
	public async Task A_refused_change_goes_back_to_the_value_it_replaced()
	{
		var refused = Change("Title", "before", "refused");
		var log = new List<Event> { Add(_facet), refused };

		var undo = new RefusedEvents().Undo(log, refused.EventId);

		await Assert.That(undo.Count).IsEqualTo(1);
		var back = (ComponentPropertyChangedEvent)undo[0];
		await Assert.That(back.NewValue).IsEqualTo("before");
		await Assert.That(back.OldValue).IsEqualTo("refused");
		await Assert.That(back.ComponentId).IsEqualTo(_facet);
		await Assert.That(back.IsLocalUndo).IsTrue();
		await Assert.That(back.StreamId).IsEqualTo(_stream);
		await Assert.That(back.EventId).IsNotEqualTo(refused.EventId);
	}

	[Test]
	public async Task A_refused_change_the_master_already_overtook_is_not_undone()
	{
		var refused = Change("Answer", null, "No");
		var masters = Change("Answer", null, "Yes");
		var log = new List<Event> { Add(_facet), refused, masters };

		var undo = new RefusedEvents().Undo(log, refused.EventId);

		await Assert.That(undo.Count).IsEqualTo(0).Because("the master's own answer came after and stands");
	}

	[Test]
	public async Task A_later_own_change_that_stands_is_not_undone()
	{
		var refused = Change("Title", "a", "b");
		var later = Change("Title", "b", "c");
		var log = new List<Event> { refused, later };

		var undo = new RefusedEvents().Undo(log, refused.EventId);

		await Assert.That(undo.Count).IsEqualTo(0);
	}

	[Test]
	public async Task Two_refused_changes_in_a_row_go_back_to_the_value_before_both()
	{
		var first = Change("Title", "kept", "first");
		var second = Change("Title", "first", "second");
		var log = new List<Event> { first, second };
		var refusals = new RefusedEvents();

		var undoFirst = refusals.Undo(log, first.EventId);
		var undoSecond = refusals.Undo(log, second.EventId);

		await Assert.That(undoFirst.Count).IsEqualTo(0).Because("the second change decides the value until it is refused too");
		await Assert.That(((ComponentPropertyChangedEvent)undoSecond.Single()).NewValue).IsEqualTo("kept");
	}

	[Test]
	public async Task A_change_after_a_kept_one_goes_back_to_the_kept_value()
	{
		var kept = Change("Title", "a", "kept");
		var refused = Change("Title", "kept", "refused");
		var log = new List<Event> { kept, refused };

		var undo = new RefusedEvents().Undo(log, refused.EventId);

		await Assert.That(((ComponentPropertyChangedEvent)undo.Single()).NewValue).IsEqualTo("kept");
	}

	[Test]
	public async Task A_refused_add_is_deleted()
	{
		var refused = Add(_facet);
		var undo = new RefusedEvents().Undo([refused], refused.EventId);

		var deleted = (ComponentDeletedEvent)undo.Single();
		await Assert.That(deleted.ComponentId).IsEqualTo(_facet);
		await Assert.That(deleted.TargetId).IsEqualTo(_thing);
		await Assert.That(deleted.IsLocalUndo).IsTrue();
	}

	[Test]
	public async Task A_refusal_heard_again_after_its_undo_is_in_the_log_undoes_nothing_more()
	{
		var refused = Change("Title", "before", "refused");
		var log = new List<Event> { Add(_facet), refused };
		var refusals = new RefusedEvents();
		log.AddRange(refusals.Undo(log, refused.EventId)); // the undo was written, the connection then ended before the cursor moved

		var again = refusals.Undo(log, refused.EventId);

		await Assert.That(again.Count).IsEqualTo(0).Because("the undo already in the log decides the value");
	}

	[Test]
	public async Task A_refused_add_deleted_since_is_not_deleted_again()
	{
		var refused = Add(_facet);
		var undo = new RefusedEvents().Undo([refused, Delete(_facet)], refused.EventId);

		await Assert.That(undo.Count).IsEqualTo(0);
	}

	[Test]
	public async Task A_refused_delete_brings_the_facet_back_with_its_kept_changes()
	{
		var add = Add(_facet);
		var kept = Change("Title", "a", "kept");
		var refusedChange = Change("Title", "kept", "refused");
		var refused = Delete(_facet);
		var log = new List<Event> { add, kept, refusedChange, refused };
		var refusals = new RefusedEvents();
		refusals.Undo(log, refusedChange.EventId);

		var undo = refusals.Undo(log, refused.EventId);

		await Assert.That(undo.Count).IsEqualTo(2);
		await Assert.That(((ComponentAddedEvent)undo[0]).ComponentId).IsEqualTo(_facet);
		await Assert.That(((ComponentPropertyChangedEvent)undo[1]).NewValue).IsEqualTo("kept");
		await Assert.That(undo.All(ev => ev.IsLocalUndo)).IsTrue();
	}

	[Test]
	public async Task A_refused_delete_of_a_thing_is_not_undone_here()
	{
		var refused = new ComponentDeletedEvent { EventId = Guid.NewGuid(), CommandId = Guid.NewGuid(), TargetId = _thing, TargetTypeId = default, CollectionId = default, ComponentId = _thing, StreamId = _stream };

		var undo = new RefusedEvents().Undo([refused], refused.EventId);

		await Assert.That(undo.Count).IsEqualTo(0);
	}

	[Test]
	public async Task An_event_the_log_does_not_hold_has_nothing_to_undo()
	{
		var refusals = new RefusedEvents();
		var missing = Guid.NewGuid();

		var undo = refusals.Undo([Add(_facet)], missing);

		await Assert.That(undo.Count).IsEqualTo(0);
		await Assert.That(refusals.Contains(missing)).IsTrue();
	}

	ComponentAddedEvent Add(Guid component) => new() { EventId = Guid.NewGuid(), CommandId = Guid.NewGuid(), TargetId = _thing, TargetTypeId = default, CollectionId = default, ComponentId = component, StreamId = _stream };

	ComponentDeletedEvent Delete(Guid component) => new() { EventId = Guid.NewGuid(), CommandId = Guid.NewGuid(), TargetId = _thing, TargetTypeId = default, CollectionId = default, ComponentId = component, StreamId = _stream };

	ComponentPropertyChangedEvent Change(string property, object? from, object? to) => new()
	{
		EventId = Guid.NewGuid(),
		CommandId = Guid.NewGuid(),
		TargetId = _thing, TargetTypeId = default, CollectionId = default,
		ComponentId = _facet,
		PropertyName = property,
		OldValue = from,
		NewValue = to,
		StreamId = _stream,
	};
}
