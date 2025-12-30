using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Synqra.Tests.TestHelpers;

public static class StringExtensions
{
	public static string NormalizeNewLines(this string input)
	{
		return input.Replace("\r\n", "\n");
	}
}
