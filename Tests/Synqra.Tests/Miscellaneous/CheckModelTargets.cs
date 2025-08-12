using Synqra.Model;
using TUnit;
using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Synqra.Tests.Miscellaneous;

internal class CheckModelTargets
{
	[Test]
	public async Task CheckModelTargetsAsync()
	{
		var actual = SynqraModelRuntimeInfo.TargetFramework;
#if NET8_0
		await Assert.That(actual).IsEqualTo("net8.0");
#elif NET9_0
		await Assert.That(actual).IsEqualTo("net9.0");
#elif NET10_0
		await Assert.That(actual).IsEqualTo("net10.0");
#else
#error "Unsupported target framework"
#endif
	}
}
