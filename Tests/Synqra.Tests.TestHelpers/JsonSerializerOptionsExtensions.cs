using System.Collections.Concurrent;
using System.Text.Json;

namespace Synqra.Tests.TestHelpers;

public static class JsonSerializerOptionsExtensions
{
	static ConcurrentDictionary<JsonSerializerOptions, JsonSerializerOptions> _known = new();

	public static JsonSerializerOptions Indented(this JsonSerializerOptions options)
	{
		if (options.IsReadOnly)
		{
			_known.GetOrAdd(options, static k => k.IndentedCore());
		}
		return options.IndentedCore();
	}

	private static JsonSerializerOptions IndentedCore(this JsonSerializerOptions options)
	{
		return new JsonSerializerOptions(options)
		{
			WriteIndented = true,
			IndentCharacter = '\t',
			IndentSize = 1,
		};
	}

	/*
	public static string ToTestJson<T>(this T item)
	{
		return JsonSerializer.Serialize(item, SampleJsonSerializerContext.Options.Indented());
	}
	*/
}
