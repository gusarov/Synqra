#if NET10_0_OR_GREATER
using Synqra.Replication.AspNetCore;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// The stream-routing rule the WS replication endpoint applies in both directions (backlog replay
/// and live broadcast). net10-only: <c>Synqra.Replication.AspNetCore</c> — like the endpoint it
/// guards — targets net10 only. See <see cref="ReplicationStreamScope"/> for why one rule covers both.
/// </summary>
public class ReplicationStreamScopeTests
{
	static readonly Guid StreamA = Guid.NewGuid();
	static readonly Guid StreamB = Guid.NewGuid();

	[Test]
	public async Task A_scoped_connection_admits_only_its_active_streams()
	{
		// The core isolation guarantee: a connection subscribed to stream A sees A's events and
		// nothing else. The own writable stream is admitted only because it is in the active set.
		var active = new HashSet<Guid> { StreamA };
		await Assert.That(ReplicationStreamScope.Admits(StreamA, StreamA, active)).IsTrue();
		await Assert.That(ReplicationStreamScope.Admits(StreamB, StreamA, active)).IsFalse();
	}

	[Test]
	public async Task A_scoped_connection_rejects_an_unstamped_or_zero_stream_event()
	{
		// A legacy/unstamped event (StreamId null or default) must NOT leak onto a scoped
		// connection — the whole point of stamping inbound events and backfilling _sid.
		var active = new HashSet<Guid> { StreamA };
		await Assert.That(ReplicationStreamScope.Admits(null, StreamA, active)).IsFalse();
		await Assert.That(ReplicationStreamScope.Admits(Guid.Empty, StreamA, active)).IsFalse();
	}

	[Test]
	public async Task An_unscoped_connection_admits_everything()
	{
		// A single-tenant host establishes no stream scope (connectionStream null) — the endpoint
		// then behaves exactly as it did before isolation: every event is delivered.
		await Assert.That(ReplicationStreamScope.Admits(StreamA, null)).IsTrue();
		await Assert.That(ReplicationStreamScope.Admits(StreamB, null)).IsTrue();
		await Assert.That(ReplicationStreamScope.Admits(null, null)).IsTrue();
		await Assert.That(ReplicationStreamScope.Admits(Guid.Empty, null)).IsTrue();
	}

	[Test]
	public async Task A_scoped_connection_admits_every_active_stream()
	{
		// Multi-stream read: a connection subscribed to both A and B sees both, but still nothing
		// else (C), and still never an unstamped/zero event.
		var active = new HashSet<Guid> { StreamA, StreamB };
		await Assert.That(ReplicationStreamScope.Admits(StreamA, StreamA, active)).IsTrue();
		await Assert.That(ReplicationStreamScope.Admits(StreamB, StreamA, active)).IsTrue();
		await Assert.That(ReplicationStreamScope.Admits(Guid.NewGuid(), StreamA, active)).IsFalse();
		await Assert.That(ReplicationStreamScope.Admits(null, StreamA, active)).IsFalse();
		await Assert.That(ReplicationStreamScope.Admits(Guid.Empty, StreamA, active)).IsFalse();
	}

	[Test]
	public async Task An_empty_active_set_admits_nothing_not_even_the_own_stream()
	{
		// The EMPTY subscription mode: a scoped connection that has subscribed to nothing yet
		// receives no events at all — not even its own stream — until it Subscribes.
		await Assert.That(ReplicationStreamScope.Admits(StreamA, StreamA, new HashSet<Guid>())).IsFalse();
		await Assert.That(ReplicationStreamScope.Admits(StreamB, StreamA, new HashSet<Guid>())).IsFalse();
		await Assert.That(ReplicationStreamScope.Admits(StreamA, StreamA, null)).IsFalse();
	}
}
#endif
