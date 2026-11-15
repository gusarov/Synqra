namespace Synqra;

/// <summary>
/// The master's answer to one of this client's own events: stored in its log, or refused for good (<see cref="Refusal"/> says
/// why). <see cref="CommandId"/> is the command that produced the event when this run sent it, else default (an event offered
/// again after a reload).
/// </summary>
public readonly record struct EventVerdict(Guid EventId, Guid CommandId, string? Refusal);
