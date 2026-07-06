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
	public async Task A_scoped_connection_admits_only_its_own_stream()
	{
		// The core isolation guarantee: a connection authorized for stream A sees A's events and
		// nothing else. Before this rule the backlog and broadcast admitted every stream.
		await Assert.That(ReplicationStreamScope.Admits(StreamA, StreamA)).IsTrue();
		await Assert.That(ReplicationStreamScope.Admits(StreamB, StreamA)).IsFalse();
	}

	[Test]
	public async Task A_scoped_connection_rejects_an_unstamped_or_zero_stream_event()
	{
		// A legacy/unstamped event (StreamId null or default) must NOT leak onto a scoped
		// connection — the whole point of stamping inbound events and backfilling _sid.
		await Assert.That(ReplicationStreamScope.Admits(null, StreamA)).IsFalse();
		await Assert.That(ReplicationStreamScope.Admits(Guid.Empty, StreamA)).IsFalse();
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
}
#endif
