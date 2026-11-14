using Microsoft.Extensions.Logging;

namespace Synqra;

/// <summary>
/// A client's own events the master refused, and the undo each needs: the state the refused event changed goes back to what the
/// local log holds without it. A refusal takes back only what nothing later decides already (the master's own event for the same
/// state, a later write of this client, a delete of the owner). An entity's delete is not taken back: its links and facets are
/// not in one event, so the next load from the master shows it again.
/// </summary>
public sealed class RefusedEvents
{
	readonly HashSet<Guid> _refused = [];

	public bool Contains(Guid eventId) => _refused.Contains(eventId);

	/// <summary>Records a refusal and returns the events that undo it, in the order to apply them (none when nothing is to undo).</summary>
	public IReadOnlyList<Event> Undo(IReadOnlyList<Event> log, Guid refusedId)
	{
		_refused.Add(refusedId);
		var at = -1;
		for (var i = 0; i < log.Count && at < 0; i++)
		{
			at = log[i].EventId == refusedId ? i : -1;
		}
		IReadOnlyList<Event> undo = at < 0 ? [] : log[at] switch
		{
			ComponentPropertyChangedEvent change => UndoChange(log, at, change),
			ComponentAddedEvent added => UndoAdd(log, at, added),
			ComponentDeletedEvent deleted when deleted.ComponentId != deleted.TargetId => UndoDelete(log, at, deleted),
			_ => [],
		};
		foreach (var ev in undo)
		{
			ev.EventId = GuidExtensions.CreateVersion7();
			ev.CommandId = ev.LocalUndoCommandId;
			ev.StreamId = log[at].StreamId;
		}
		return undo;
	}

	IReadOnlyList<Event> UndoChange(IReadOnlyList<Event> log, int at, ComponentPropertyChangedEvent change)
	{
		// Before the refused change: the last earlier change the master kept, else the value the first change replaced.
		var value = change.OldValue;
		var found = false;
		for (var i = at - 1; i >= 0 && !found; i--)
		{
			if (log[i] is ComponentPropertyChangedEvent earlier && Changes(earlier, change.ComponentId, change.PropertyName))
			{
				found = !_refused.Contains(earlier.EventId);
				value = found ? earlier.NewValue : earlier.OldValue;
			}
		}
		var decidedLater = DecidedAfter(log, at, ev => ev is ComponentPropertyChangedEvent later && Changes(later, change.ComponentId, change.PropertyName) || Removes(ev, change.ComponentId, change.TargetId));
		return decidedLater || Equals(value, change.NewValue)
			? []
			: [new ComponentPropertyChangedEvent
			{
				EventId = default,
				CommandId = default,
				TargetId = change.TargetId,
				TargetTypeId = change.TargetTypeId,
				CollectionId = change.CollectionId,
				ComponentTypeId = change.ComponentTypeId,
				ComponentId = change.ComponentId,
				PropertyName = change.PropertyName,
				OldValue = change.NewValue,
				NewValue = value,
			}];
	}

	IReadOnlyList<Event> UndoAdd(IReadOnlyList<Event> log, int at, ComponentAddedEvent added)
		=> DecidedAfter(log, at, ev => Removes(ev, added.ComponentId, added.TargetId))
			? []
			: [new ComponentDeletedEvent
			{
				EventId = default,
				CommandId = default,
				TargetId = added.TargetId,
				TargetTypeId = added.TargetTypeId,
				CollectionId = added.CollectionId,
				ComponentTypeId = added.ComponentTypeId,
				ComponentId = added.ComponentId,
			}];

	/// <summary>A facet or a link comes back as it was: its add again, then every change the master kept.</summary>
	IReadOnlyList<Event> UndoDelete(IReadOnlyList<Event> log, int at, ComponentDeletedEvent deleted)
	{
		var undo = new List<Event>();
		var decidedLater = DecidedAfter(log, at, ev => ev is ComponentAddedEvent again && again.ComponentId == deleted.ComponentId || Removes(ev, deleted.TargetId, deleted.TargetId));
		var added = -1;
		for (var i = at - 1; i >= 0 && added < 0 && !decidedLater; i--)
		{
			added = log[i] is ComponentAddedEvent add && add.ComponentId == deleted.ComponentId && !_refused.Contains(add.EventId) ? i : -1;
		}
		if (added >= 0)
		{
			var add = (ComponentAddedEvent)log[added];
			undo.Add(new ComponentAddedEvent
			{
				EventId = default,
				CommandId = default,
				TargetId = add.TargetId,
				TargetTypeId = add.TargetTypeId,
				CollectionId = add.CollectionId,
				ComponentTypeId = add.ComponentTypeId,
				ComponentId = add.ComponentId,
				Data = add.Data,
			});
			for (var i = added + 1; i < at; i++)
			{
				if (log[i] is ComponentPropertyChangedEvent kept && kept.ComponentId == deleted.ComponentId && !_refused.Contains(kept.EventId))
				{
					undo.Add(new ComponentPropertyChangedEvent
					{
						EventId = default,
						CommandId = default,
						TargetId = kept.TargetId,
						TargetTypeId = kept.TargetTypeId,
						CollectionId = kept.CollectionId,
						ComponentTypeId = kept.ComponentTypeId,
						ComponentId = kept.ComponentId,
						PropertyName = kept.PropertyName,
						OldValue = kept.OldValue,
						NewValue = kept.NewValue,
					});
				}
			}
		}
		else if (!decidedLater)
		{
			EmergencyLog.Default.LogWarning($"RefusedEvents: the refused delete {deleted.EventId} of {deleted.ComponentId} has no add in the local log (a snapshot holds it); the next load from the master shows it again.");
		}
		return undo;
	}

	/// <summary>Whether an event after the refused one, which the master did not refuse, decides the same state.</summary>
	bool DecidedAfter(IReadOnlyList<Event> log, int at, Func<Event, bool> decides)
	{
		var decided = false;
		for (var i = at + 1; i < log.Count && !decided; i++)
		{
			decided = !_refused.Contains(log[i].EventId) && decides(log[i]);
		}
		return decided;
	}

	bool Changes(ComponentPropertyChangedEvent ev, Guid componentId, string property) => ev.ComponentId == componentId && ev.PropertyName == property;

	bool Removes(Event ev, Guid componentId, Guid ownerId) => ev is ComponentDeletedEvent deleted && (deleted.ComponentId == componentId || deleted.ComponentId == ownerId);
}
