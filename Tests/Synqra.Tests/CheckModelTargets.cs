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
		var actual = SynqraRuntimeInfo.SynqraModelTarget;
		await Assert.That(actual).IsEqualTo("net9.0");
	}
}
