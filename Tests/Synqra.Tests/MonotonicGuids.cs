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
			Console.WriteLine(Guid.CreateVersion7());
		}


		Guid prevId = default;
		var sw = Stopwatch.StartNew();
		for (var newId = Guid.CreateVersion7(); sw.ElapsedMilliseconds < 3000 ;)
		{
			Console.WriteLine(newId);
			await Assert.That(newId.CompareTo(prevId)).IsEqualTo(1);
			prevId = newId;
		}
	}
}
