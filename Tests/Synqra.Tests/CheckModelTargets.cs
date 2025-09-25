using Synqra;
using TUnit;
using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Synqra.Tests;

internal class CheckModelTargets
{
	[Test]
	public async Task CheckModelTargetsAsync()
	{
		var actual = SynqraTargetInfo.TargetFramework;
#if NETFRAMEWORK
		await Assert.That(actual).IsEqualTo("netstandard2.0"); // because there is no net48, only standard2.0
#elif NET8_0
		await Assert.That(actual).IsEqualTo("net8.0");
#elif NET9_0
		await Assert.That(actual).IsEqualTo("net9.0");
#endif
	}
}
