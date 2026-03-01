using Microsoft.Extensions.Logging;
using System.Buffers.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

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

public static class SynqraNativeAOT
{
	static bool? _isUnusedFieldStayAssigned = true;

	static bool IsTrimmed()
	{
		return null == typeof(SynqraNativeAOT).GetField(/*nameof(*/"_isUnusedFieldStayAssigned"/*)*/, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
	}


	public static bool IsNativeAOT
	{
		get
		{
			var isTrimmed = IsTrimmed();
			var isDynamicCodeCompiled =
#if NET8_0_OR_GREATER
				!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled
#else
				false
#endif
			;
			EmergencyLog.Default.LogWarning($"IsNativeAOT: isTrimmed: {isTrimmed}, isDynamicCodeCompiled: {isDynamicCodeCompiled}");
			return isTrimmed && isDynamicCodeCompiled;
		}
	}
}
