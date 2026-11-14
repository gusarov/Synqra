namespace Synqra.Replication.AspNetCore;

/// <summary>
/// A peer's event the log did not store. It ends the connection, never acknowledged: the sender's cursor stays before it and
/// its next connection offers it again, where a later event's ack on this one would have moved the cursor past it.
/// </summary>
internal sealed class UnstoredEventException(Event ev, Exception inner)
	: Exception($"Synqra replication: event {ev.EventId} of stream {ev.StreamId} was not stored; the connection ends so the sender offers it again.", inner);
