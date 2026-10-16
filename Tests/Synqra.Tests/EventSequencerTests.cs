using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// The server's commit order (docs/model.md §9): a proposal that follows is kept, one behind the stream or too far
/// ahead of the clock is fast-forwarded, and the watermark trails the lowest stamp still in flight.
/// </summary>
public class EventSequencerTests
{
	[Test]
	public async Task Should_keep_a_proposal_that_follows_the_stream()
	{
		var sequencer = new EventSequencer(Options.Create(new EventSequencerOptions()));
		var stream = Guid.NewGuid();
		sequencer.Seed(stream, default);
		var first = GuidExtensions.CreateVersion7();
		var second = GuidExtensions.CreateVersion7();

		await Assert.That(sequencer.Stamp(stream, first)).IsEqualTo(first);
		await Assert.That(sequencer.Stamp(stream, second)).IsEqualTo(second);
	}

	[Test]
	public async Task Should_fast_forward_a_proposal_behind_the_last_stamp()
	{
		var sequencer = new EventSequencer(Options.Create(new EventSequencerOptions()));
		var stream = Guid.NewGuid();
		sequencer.Seed(stream, default);
		var older = GuidExtensions.CreateVersion7();
		var newer = GuidExtensions.CreateVersion7();
		sequencer.Stamp(stream, newer);

		var stamped = sequencer.Stamp(stream, older);

		await Assert.That(stamped).IsNotEqualTo(older);
		await Assert.That(stamped.CompareBigEndian(newer)).IsGreaterThan(0);
		await Assert.That(stamped.GetVersion()).IsEqualTo(7);
	}

	[Test]
	public async Task Should_replace_a_proposal_running_ahead_of_the_clock()
	{
		var sequencer = new EventSequencer(Options.Create(new EventSequencerOptions { MaxAhead = TimeSpan.FromMinutes(5), }));
		var stream = Guid.NewGuid();
		sequencer.Seed(stream, default);
		var future = Shift(GuidExtensions.CreateVersion7(), TimeSpan.FromMinutes(6));

		var stamped = sequencer.Stamp(stream, future);

		await Assert.That(stamped).IsNotEqualTo(future);
		await Assert.That(stamped.GetTimestamp()).IsLessThan(DateTime.UtcNow.AddMinutes(1));
		await Assert.That(sequencer.Stamp(stream, Shift(GuidExtensions.CreateVersion7(), TimeSpan.FromMinutes(4)))).IsNotEqualTo(stamped); // within the tolerance: kept
	}

	[Test]
	public async Task Should_replace_a_proposal_that_is_not_a_clock_id()
	{
		var sequencer = new EventSequencer(Options.Create(new EventSequencerOptions()));
		var stream = Guid.NewGuid();
		sequencer.Seed(stream, default);
		var structured = new Guid("C0DE0000-0000-8000-8E01-000000000001");

		var stamped = sequencer.Stamp(stream, structured);

		await Assert.That(stamped).IsNotEqualTo(structured);
		await Assert.That(stamped.GetVersion()).IsEqualTo(7);
	}

	[Test]
	public async Task Should_follow_the_seeded_tail()
	{
		var sequencer = new EventSequencer(Options.Create(new EventSequencerOptions()));
		var stream = Guid.NewGuid();
		var tail = GuidExtensions.CreateVersion7();
		var behind = Shift(tail, TimeSpan.FromMilliseconds(-10));
		sequencer.Seed(stream, tail);

		var stamped = sequencer.Stamp(stream, behind);

		await Assert.That(stamped.CompareBigEndian(tail)).IsGreaterThan(0);
		await Assert.That(sequencer.ReadWatermark(stream)).IsEqualTo(tail);
	}

	[Test]
	public async Task Should_refuse_to_stamp_a_stream_that_is_not_seeded()
	{
		var sequencer = new EventSequencer(Options.Create(new EventSequencerOptions()));

		Assert.Throws<InvalidOperationException>(() => sequencer.Stamp(Guid.NewGuid(), GuidExtensions.CreateVersion7()));
		await Task.CompletedTask;
	}

	[Test]
	public async Task Should_hold_the_watermark_below_a_stamp_still_in_flight()
	{
		var sequencer = new EventSequencer(Options.Create(new EventSequencerOptions()));
		var stream = Guid.NewGuid();
		sequencer.Seed(stream, default);
		var a = sequencer.Stamp(stream, GuidExtensions.CreateVersion7());
		var b = sequencer.Stamp(stream, GuidExtensions.CreateVersion7());

		sequencer.Resolve(stream, [b], committed: true);
		await Assert.That(sequencer.ReadWatermark(stream)).IsEqualTo(default(Guid)); // a is still in flight

		sequencer.Resolve(stream, [a], committed: true);
		await Assert.That(sequencer.ReadWatermark(stream)).IsEqualTo(b);
	}

	[Test]
	public async Task Should_let_an_aborted_stamp_go()
	{
		var sequencer = new EventSequencer(Options.Create(new EventSequencerOptions()));
		var stream = Guid.NewGuid();
		sequencer.Seed(stream, default);
		var a = sequencer.Stamp(stream, GuidExtensions.CreateVersion7());
		var b = sequencer.Stamp(stream, GuidExtensions.CreateVersion7());

		sequencer.Resolve(stream, [a], committed: false);
		sequencer.Resolve(stream, [b], committed: true);

		await Assert.That(sequencer.ReadWatermark(stream)).IsEqualTo(b);
	}

	[Test]
	public async Task Should_report_the_commits_watermark_inside_the_callback()
	{
		var sequencer = new EventSequencer(Options.Create(new EventSequencerOptions()));
		var stream = Guid.NewGuid();
		sequencer.Seed(stream, default);
		var a = sequencer.Stamp(stream, GuidExtensions.CreateVersion7());
		Guid seen = default;

		sequencer.Resolve(stream, [a], committed: true, () => seen = sequencer.ReadWatermark(stream));

		await Assert.That(seen).IsEqualTo(a);
	}

	[Test]
	public async Task Should_order_ids_by_their_bytes()
	{
		var before = new Guid("01a13d91-7fff-7000-8000-000000000000");
		var after = new Guid("01a13d91-8000-7000-8000-000000000000"); // the second group's top bit flips every 65 s: a signed compare would misorder it

		await Assert.That(after.CompareBigEndian(before)).IsGreaterThan(0);
		await Assert.That(before.CompareBigEndian(after)).IsLessThan(0);
	}

	/// <summary>The same v7 id with its millisecond timestamp moved by <paramref name="by"/>.</summary>
	Guid Shift(Guid v7, TimeSpan by)
	{
		var bytes = v7.ToByteArray(bigEndian: true);
		long ms = 0;
		for (var i = 0; i < 6; i++)
		{
			ms = (ms << 8) | bytes[i];
		}
		ms += (long)by.TotalMilliseconds;
		for (var i = 5; i >= 0; i--)
		{
			bytes[i] = (byte)(ms & 0xFF);
			ms >>= 8;
		}
		return new Guid(bytes, bigEndian: true);
	}
}
