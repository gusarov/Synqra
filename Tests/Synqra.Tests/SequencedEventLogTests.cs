using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.AppendStorage.InMemory;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// The sequenced log over an in-memory log: every append is stamped, reported with the watermark it was committed
/// under, and read back in commit order (docs/model.md §9).
/// </summary>
public class SequencedEventLogTests : BaseTest
{
	readonly Guid _stream = Guid.NewGuid();

	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.Services.AddAppendStorageInMemory<Event, Guid>(x => x.SequenceId);
		hostApplicationBuilder.Services.AddSequencedEventLog();
	}

	SequencedEventLog Log => (SequencedEventLog)ServiceProvider.GetRequiredService<IAppendStorage<Event, Guid>>();

	ObjectPropertyChangedEvent Change(Guid id, string value) => new()
	{
		EventId = id,
		CommandId = GuidExtensions.CreateVersion7(),
		StreamId = _stream,
		TargetId = Guid.NewGuid(),
		TargetTypeId = Guid.NewGuid(),
		CollectionId = Guid.NewGuid(),
		PropertyName = "Name",
		NewValue = value,
	};

	[Test]
	public async Task Should_stamp_a_fresh_event_with_its_own_id_and_report_it()
	{
		var log = Log;
		var ev = Change(GuidExtensions.CreateVersion7(), "Alice");
		var reported = new List<Event>();
		Guid watermark = default;
		log.EventsCommitted += events =>
		{
			reported.AddRange(events);
			watermark = log.ReadWatermark(_stream);
		};

		await log.AppendAsync(ev);

		await Assert.That(ev.SequenceId).IsEqualTo(ev.EventId);
		await Assert.That(reported).HasSingleItem();
		await Assert.That(watermark).IsEqualTo(ev.EventId);
	}

	[Test]
	public async Task Should_fast_forward_an_event_behind_the_stream_and_read_in_commit_order()
	{
		var log = Log;
		var older = Change(GuidExtensions.CreateVersion7(), "older");
		var newer = Change(GuidExtensions.CreateVersion7(), "newer");

		await log.AppendAsync(newer);
		await log.AppendAsync(older);

		await Assert.That(older.SequenceId).IsNotEqualTo(older.EventId);
		await Assert.That(older.SequenceId.CompareBigEndian(newer.SequenceId)).IsGreaterThan(0);
		var read = log.GetAllAsync().ToBlockingEnumerable().Select(e => e.EventId).ToArray();
		await Assert.That(read).IsEquivalentTo(new[] { newer.EventId, older.EventId });
	}

	[Test]
	public async Task Should_stamp_a_batch_in_order_and_report_it_once()
	{
		var log = Log;
		var batch = new[] { Change(GuidExtensions.CreateVersion7(), "a"), Change(GuidExtensions.CreateVersion7(), "b"), Change(GuidExtensions.CreateVersion7(), "c") };
		var signals = 0;
		Guid watermark = default;
		log.EventsCommitted += events =>
		{
			signals++;
			watermark = log.ReadWatermark(_stream);
		};

		await log.AppendBatchAsync(batch);

		await Assert.That(signals).IsEqualTo(1);
		await Assert.That(batch[1].SequenceId.CompareBigEndian(batch[0].SequenceId)).IsGreaterThan(0);
		await Assert.That(batch[2].SequenceId.CompareBigEndian(batch[1].SequenceId)).IsGreaterThan(0);
		await Assert.That(watermark).IsEqualTo(batch[2].SequenceId);
	}

	[Test]
	public async Task Should_refuse_a_batch_across_streams()
	{
		var log = Log;
		var foreign = Change(GuidExtensions.CreateVersion7(), "x");
		foreign.StreamId = Guid.NewGuid();

		await Assert.ThrowsAsync<InvalidOperationException>(() => log.AppendBatchAsync([Change(GuidExtensions.CreateVersion7(), "a"), foreign]));
	}

	[Test]
	public async Task Should_let_a_failed_append_go_so_the_watermark_moves_on()
	{
		var inner = new FailingOnce(ServiceProvider.GetRequiredService<IAppendStorage<Event, Guid>>());
		var log = new SequencedEventLog(inner, new EventSequencer(Options.Create(new EventSequencerOptions())));
		var lost = Change(GuidExtensions.CreateVersion7(), "lost");
		var kept = Change(GuidExtensions.CreateVersion7(), "kept");

		await Assert.ThrowsAsync<IOException>(() => log.AppendAsync(lost));
		await log.AppendAsync(kept);

		await Assert.That(log.ReadWatermark(_stream)).IsEqualTo(kept.SequenceId);
	}

	/// <summary>An inner log whose first append fails after the stamp was issued.</summary>
	sealed class FailingOnce(IAppendStorage<Event, Guid> inner) : IAppendStorage<Event, Guid>
	{
		bool _failed;

		public async Task AppendAsync(Event item, CancellationToken cancellationToken = default)
		{
			if (!_failed)
			{
				_failed = true;
				throw new IOException("the disk is full");
			}
			await inner.AppendAsync(item, cancellationToken);
		}

		public Task<Event> GetAsync(Guid key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);

		public IAsyncEnumerable<Event> GetAllAsync(Guid from = default, CancellationToken cancellationToken = default) => inner.GetAllAsync(from, cancellationToken);

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
