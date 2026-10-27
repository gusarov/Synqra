using System.Buffers;
using System.Reflection;
using Synqra.BinarySerializer;
using Synqra.BinarySerializer.Tests;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.BinarySerialization;

/// <summary>
/// A frame larger than the buffer it is first written into: the writer grows the buffer until the frame fits
/// (prod 2026-10-19: a 450-command batch did not fit the 8 KB replication frame, and every replay died on it).
/// </summary>
public class SbxFrameTests : SbxBaseTest
{
	const int FirstBuffer = 8 * 1024;

	// Distinct strings defeat interning, so the frame is about as long as the text.
	static List<string> ManyStrings(int count) => Enumerable.Range(0, count).Select(i => $"status line {i:D6} of a scanned host that is ready").ToList();

	SbxSerializer CreateSerializer()
	{
		var ser = new SbxSerializer();
		ser.Snapshot(); // a retry resets to this
		return ser;
	}

	[Test]
	public async Task A_frame_that_fits_comes_back_in_the_callers_buffer()
	{
		var ser = CreateSerializer();
		var buffer = new byte[FirstBuffer];

		var frame = ser.SerializeFrame(ManyStrings(10), buffer);

		await Assert.That(frame.Array).IsSameReferenceAs(buffer);
		await Assert.That(frame.Count).IsGreaterThan(0).And.IsLessThan(FirstBuffer);
	}

	[Test]
	public async Task A_frame_larger_than_the_buffer_grows_and_round_trips()
	{
		var ser = CreateSerializer();
		var strings = ManyStrings(1000);

		var frame = ser.SerializeFrame(strings, new byte[FirstBuffer]);

		await Assert.That(frame.Count).IsGreaterThan(FirstBuffer);
		var pos = 0;
		var back = CreateSerializer().Deserialize<List<string>>(frame.AsSpan(), ref pos);
		await Assert.That(pos).IsEqualTo(frame.Count);
		await Assert.That(back).IsEquivalentTo(strings);
	}

	[Test]
	public async Task A_frame_past_the_limit_is_refused_with_the_overflow_attached()
	{
		var ser = CreateSerializer();

		var refused = await Assert.ThrowsAsync<SbxFrameTooLargeException>(() => Task.FromResult(ser.SerializeFrame(ManyStrings(1000), new byte[FirstBuffer], maxFrameSize: 2 * FirstBuffer)));

		await Assert.That(refused.Limit).IsEqualTo(2 * FirstBuffer);
		await Assert.That(refused.InnerException).IsNotNull();
		await Assert.That(refused.InnerException!.IsBufferOverflow()).IsTrue();
	}

	[Test]
	public async Task A_pooled_frame_round_trips_and_its_array_is_the_callers_to_return()
	{
		var ser = CreateSerializer();
		var strings = ManyStrings(1000);
		var pool = ArrayPool<byte>.Shared;

		var frame = ser.SerializeFrame(strings, pool.Rent(FirstBuffer), pool);
		try
		{
			await Assert.That(frame.Count).IsGreaterThan(FirstBuffer);
			var pos = 0;
			await Assert.That(CreateSerializer().Deserialize<List<string>>(frame.AsSpan(), ref pos)).IsEquivalentTo(strings);
		}
		finally
		{
			pool.Return(frame.Array!);
		}
	}

	/// <summary>
	/// Mono's WASM AOT runs the exception filter of a generic method through the interpreter, and that trapped with
	/// "memory access out of bounds" on every browser page load that sent a frame over 8 KB (dev and prod 2026-10-26).
	/// </summary>
	[Test]
	public async Task No_generic_method_of_the_serializer_catches_through_an_exception_filter()
	{
		var filtered = new[] { typeof(SbxFrameExtensions).Assembly, typeof(SbxSerializer).Assembly }
			.SelectMany(a => a.GetTypes())
			.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
				.Where(m => m.IsGenericMethodDefinition || t.IsGenericTypeDefinition)
				.Where(m => m.GetMethodBody()?.ExceptionHandlingClauses.Any(c => c.Flags == ExceptionHandlingClauseOptions.Filter) == true)
				.Select(m => $"{t.FullName}.{m.Name}"))
			.ToList();

		await Assert.That(filtered).IsEmpty();
	}

	[Test]
	public async Task A_try_that_overflows_leaves_the_serializer_clean_for_the_next_frame()
	{
		var ser = CreateSerializer();
		var strings = ManyStrings(1000);

		var fits = ser.TrySerializeFrame(strings, stackalloc byte[FirstBuffer], out var written);
		await Assert.That(fits).IsFalse();
		await Assert.That(written).IsEqualTo(0);

		// the retry must not reuse strings the overflowed attempt interned
		var frame = ser.SerializeFrame(strings, new byte[FirstBuffer]);
		var pos = 0;
		await Assert.That(CreateSerializer().Deserialize<List<string>>(frame.AsSpan(), ref pos)).IsEquivalentTo(strings);
	}
}
