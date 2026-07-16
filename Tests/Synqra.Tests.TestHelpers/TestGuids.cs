using System;
using System.Threading;

namespace Synqra;

/// <summary>
/// Process-wide auto-incrementing guids for unit tests — the <b>A</b> variant
/// (<c>C0DE0000-0000-8000-A000-{counter}</c>): internal company (<c>0000</c> hash), v8, variant
/// nibble <c>A</c> = test-auto, class <c>000</c>, monotonic counter tail. See docs/model.md §8.
/// <para>
/// Use this in place of <see cref="GuidExtensions.CreateVersion7"/> when a test needs an id but does
/// not care about its exact value — it stays predictable and visible instead of random. Contrast with
/// hand-written <b>9</b> (hardcoded test) guids and <b>8</b> (prod / manual) guids.
/// </para>
/// </summary>
public static class TestGuids
{
	static long _counter;

	/// <summary>Next process-wide auto-incremented test guid: <c>C0DE0000-0000-8000-A000-{n:X12}</c>.</summary>
	public static Guid NewAuto() => new Guid($"C0DE0000-0000-8000-A000-{Interlocked.Increment(ref _counter):X12}");
}
