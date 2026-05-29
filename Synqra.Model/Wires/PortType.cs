namespace Synqra;

/// <summary>
/// Closed set of port channel types the substrate knows about. Different port
/// types have different routing semantics, but the wire entity carrying them is
/// the same shape. Phase 1 ships with <see cref="Event"/> only; other types stub
/// out the type system so callers can declare their intent today and the
/// runtime can grow into each channel without re-shaping the wire model.
/// </summary>
public enum PortType
{
	/// <summary>Discrete async messages with FIFO + retry semantics (cron, webhooks, agent steps).</summary>
	Event = 0,

	/// <summary>Combinational booleans, instant on change (logic gates, "is healthy").</summary>
	Signal = 1,

	/// <summary>Rate-based flows with unit accounting (electricity, AI tokens, money).</summary>
	Quantity = 2,

	/// <summary>Static topology link; no runtime value (depends-on, lives-in).</summary>
	Reference = 3,

	/// <summary>Push subscriptions with backpressure (log tails, MQTT, SSE).</summary>
	Stream = 4,
}
