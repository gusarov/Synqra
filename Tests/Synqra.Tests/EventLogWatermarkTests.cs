using Microsoft.Extensions.DependencyInjection;
using Synqra.AppendStorage;
using Synqra.AppendStorage.InMemory;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// A server log in a process that has not written a stream yet knows nothing of its watermark until it reads the log's
/// tail: the replication endpoint asks for the seeded value before a replay, so a client resuming after a restart of
/// the server gets a real position (docs/model.md §9). The same log answers whether it holds an event by identity.
/// </summary>
public class EventLogWatermarkTests
{
	[Test]
	public async Task Should_seed_the_watermark_from_the_tail_on_the_first_read()
	{
		var stream = Guid.NewGuid();
		var services = BuildServerLog();
		var tail = GuidExtensions.CreateVersion7();
		await services.GetRequiredService<IAppendStorage<Event, (Guid, Guid)>>().AppendAsync(NewEvent(stream, tail, tail)); // stored by an earlier run of the process
		var log = (IEventWatermarks)services.GetRequiredService<IEventLog>();

		await Assert.That(log.ReadWatermark(stream)).IsEqualTo(default(Guid)).Because("nothing was stamped or read in this process yet");
		await Assert.That(await log.ReadWatermarkAsync(stream)).IsEqualTo(tail);
		await Assert.That(log.ReadWatermark(stream)).IsEqualTo(tail).Because("seeded once, the sync read answers too");
	}

	[Test]
	public async Task Should_find_an_event_the_log_holds_by_its_identity()
	{
		var stream = Guid.NewGuid();
		var services = BuildServerLog();
		var log = services.GetRequiredService<IEventLog>();
		var held = GuidExtensions.CreateVersion7();
		await log.AppendAsync(NewEvent(stream, held, default));
		var lookup = (IEventLookup)log;

		await Assert.That(await lookup.ContainsAsync(stream, held)).IsTrue();
		await Assert.That(await lookup.ContainsAsync(stream, Guid.NewGuid())).IsFalse();
		await Assert.That(await lookup.ContainsAsync(Guid.NewGuid(), held)).IsFalse().Because("identity is per stream");
	}

	static ServiceProvider BuildServerLog()
	{
		var services = new ServiceCollection();
		services.AddAppendStorageInMemory<Event, (Guid, Guid)>(ev => (ev.StreamId, ev.SequenceId));
		services.AddServerEventLog();
		return services.BuildServiceProvider();
	}

	static Event NewEvent(Guid stream, Guid eventId, Guid sequence) => new ObjectDeletedEvent
	{
		EventId = eventId,
		SequenceId = sequence,
		StreamId = stream,
		CommandId = eventId,
		TargetId = Guid.NewGuid(),
		TargetTypeId = Guid.NewGuid(),
		CollectionId = Guid.NewGuid(),
	};
}
