using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

static class PropertySetterExtensions
{
	/*
	public static string ToPascal(this string name)
	{
		return char.ToUpperInvariant(name[0]) + name.Substring(1);
	}

	public static string ToCamel(this string name)
	{
		return char.ToLowerInvariant(name[0]) + name.Substring(1);
	}
	*/

	public static void RSetSTJ<T>(this T obj, string json, JsonSerializerContext jsonSerializerContext)
	{
		var so = new JsonSerializerOptions(jsonSerializerContext.Options);
		var ti = so.GetTypeInfo(obj.GetType());

#if DEBUG
		var so2 = new JsonSerializerOptions(jsonSerializerContext.Options);
		var ti2 = so.GetTypeInfo(obj.GetType());
		if (ReferenceEquals(ti2, ti))
		{
			throw new Exception($"Can't get isolated type info!");
		}
#endif

		ti.CreateObject = () => obj;
		var patched = JsonSerializer.Deserialize(json, ti);
		if (!ReferenceEquals(obj, null))
		{
			if (!ReferenceEquals(patched, obj))
			{
				throw new Exception("Failed to patch existing object");
			}
		}
	}

	public static void RSetSTJ<T>(this T obj, string property, object value, JsonSerializerContext jsonSerializerContext)
	{
		IDictionary<string, object> dic = new Dictionary<string, object>
		{
			[property] = value,
		};
		var json = JsonSerializer.Serialize(dic, jsonSerializerContext.Options);
		obj.RSetSTJ(json, jsonSerializerContext);
	}

}