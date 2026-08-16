using System.Text.Json.Serialization;

namespace Synqra;

[JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators = false, TypeDiscriminatorPropertyName = "_t", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(EventEnvelope), "EventEnvelope")]
[JsonDerivedType(typeof(SubscribeRequest), "SubscribeRequest")]
[JsonDerivedType(typeof(UnsubscribeRequest), "UnsubscribeRequest")]
[JsonDerivedType(typeof(SubscriptionState), "SubscriptionState")]
[SynqraModel("C0DEADD0-1032-8000-9A01-000000000000")] // transport/infra family A — PROVISIONAL placement (test/temp space)
[Schema(2025.791, "1")]
public abstract partial class TransportOperation
{
}

/// <summary>
/// Carries exactly one <see cref="Synqra.Event"/> across the wire. Deliberately directionless: a client
/// sends it to submit a locally-produced event, and the master sends it to broadcast or to replay a
/// stream's backlog. Everything about routing is decided from the event's own StreamId plus the
/// connection's active subscription set, never from the envelope.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A02-000000000000")] // transport/infra family A — PROVISIONAL placement (test/temp space)
[Schema(2025.785, "1 Event Event")]
public partial class EventEnvelope : TransportOperation
{
	public required partial Event Event { get; set; }

	public override string ToString()
	{
		return Event.ToString();
	}
}

/// <summary>
/// Client → master: "start delivering this stream to me". The master authorizes it against the
/// connection's host-granted ceiling, replays that stream's backlog, then answers with a
/// <see cref="SubscriptionState"/>; a rejected request simply leaves the active set unchanged,
/// which that answer reveals.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A03-000000000000")]
[Schema(2026.616, "1 StreamId Guid")] // transport/infra family A — PROVISIONAL placement (test/temp space)
public partial class SubscribeRequest : TransportOperation
{
	public required partial Guid StreamId { get; set; }

	public override string ToString() => $"Subscribe({StreamId})";
}

/// <summary>Client → master: "stop delivering this stream to me". Answered with a <see cref="SubscriptionState"/>.</summary>
[SynqraModel("C0DEADD0-1032-8000-9A04-000000000000")]
[Schema(2026.616, "1 StreamId Guid")] // transport/infra family A — PROVISIONAL placement (test/temp space)
public partial class UnsubscribeRequest : TransportOperation
{
	public required partial Guid StreamId { get; set; }

	public override string ToString() => $"Unsubscribe({StreamId})";
}

/// <summary>
/// Master → client: the authoritative snapshot of the streams this connection is now subscribed to
/// (may be empty). Sent right after HELLO and after every <see cref="SubscribeRequest"/> /
/// <see cref="UnsubscribeRequest"/>, so a client can compare it against what it asked for and detect an
/// unexpected server default or a rejected request.
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-9A05-000000000000")]
[Schema(2026.616, "1 Streams IList<Guid>")] // transport/infra family A — PROVISIONAL placement (test/temp space)
public partial class SubscriptionState : TransportOperation
{
	public partial IList<Guid> Streams { get; set; }

	public override string ToString() => $"SubscriptionState([{string.Join(", ", Streams)}])";
}
