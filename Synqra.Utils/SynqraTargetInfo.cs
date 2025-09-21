using System.ComponentModel;

namespace Synqra;

public static class SynqraTargetInfo
{
	public static string TargetFramework =>
#if NETSTANDARD2_0
		"netstandard2.0"
#elif NETSTANDARD2_1
		"netstandard2.1"
#elif NET8_0
		"net8.0"
#elif NET9_0
		"net9.0"
#elif NET10_0
		"net10.0"
#else
#error Please add target framework moniker here
#endif
		;
}
