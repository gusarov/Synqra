using System.Runtime.CompilerServices;

namespace Synqra.Tests;

/// <summary>
/// Switches <see cref="Ids"/> into deterministic test mode for the whole test run, so ids minted by
/// production code paths under test come out as readable, monotonic, per-class <b>A</b>-variant values
/// (<c>C0DE0000-0000-8000-A{class}-…</c>) instead of random v7 noise. Static + process-wide, so it also
/// covers WAF hosts and background-service threads. Tests that exercise
/// <see cref="GuidExtensions.CreateVersion7"/> / <see cref="GuidExtensions.Derive"/> directly are
/// unaffected — those bypass <see cref="Ids"/>.
/// </summary>
internal static class TestGuidsModuleInit
{
	[ModuleInitializer]
	internal static void Init() => Ids.DeterministicTestIds = true;
}
