using Synqra.BinarySerializer;
using System.Text.Json;

namespace Synqra.BinarySerializer.Tests;

/// <summary>
/// Minimal base class for SBX serializer tests.
/// Provides HexDump without pulling in DI/hosting/MongoDB infrastructure.
/// </summary>
public class SbxBaseTest
{
	public HexDumpWriter HexDumpWriter = new HexDumpWriter();

	public void HexDump(ReadOnlySpan<byte> data, SbxSerializer? serializer = null)
	{
		Console.WriteLine();
		HexDumpWriter.HexDump(data, Console.Write, Console.Write);
		Console.WriteLine();

#if DEBUG
		if (serializer is not null)
		{
			Console.WriteLine("Tokenized:");
			foreach (var item in serializer.Tokens)
			{
				Console.WriteLine(item.Item3);
				HexDump(data[item.Item1..(item.Item2 - 1)]);
			}
			Console.WriteLine();
		}
#endif

		Console.WriteLine();
	}

	public void HexDumpSmall(ReadOnlySpan<byte> data)
	{
		HexDumpWriter.HexDumpSmall(data, Console.Write, Console.Write);
	}
}

public static class JsonSerializerOptionsExtensions
{
	public static JsonSerializerOptions Indented(this JsonSerializerOptions options)
	{
		return new JsonSerializerOptions(options)
		{
			WriteIndented = true,
			IndentCharacter = '\t',
			IndentSize = 1,
		};
	}
}
