namespace Synqra;

/// <summary>
/// Per-replay-loop or per-process context passed to <see cref="IEventVisitor{T}"/>
/// implementations. Carries scalar flags shared by every event in the loop.
/// </summary>
public class EventVisitorContext
{
	/// <summary>
	/// <c>true</c> when the projection is rebuilding from the event store
	/// (replay mode). Activators that have one-shot side effects
	/// (subscriptions, webhook tokens, etc.) check this and skip those.
	/// </summary>
	public bool IsReplay { get; set; }
}
