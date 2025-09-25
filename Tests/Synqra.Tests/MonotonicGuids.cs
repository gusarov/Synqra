using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Synqra.Tests;

internal class MonotonicGuids
{
	[Test]
	public async Task Should_generate_sortable_guids()
	{
		for (int i = 0; i < 10; i++)
		{
			// Console.WriteLine(GuidExtensions.CreateVersion7());
		}


		Guid prevId = GuidExtensions.CreateVersion7();
		var sw = Stopwatch.StartNew();
		int q = 0;
		while (sw.ElapsedMilliseconds < 3000)
		{
			var newId = GuidExtensions.CreateVersion7();
			q++;
			// Console.WriteLine(newId);
			await Assert.That(newId.CompareTo(prevId)).IsEqualTo(1);
			prevId = newId;
		}
	}
}
