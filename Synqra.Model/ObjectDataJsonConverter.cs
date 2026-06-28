using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

public sealed class ObjectDataJsonConverter : JsonConverter<ObjectData>
{
	public override ObjectData Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.StartObject)
		{
			throw new JsonException($"Expected {JsonTokenType.StartObject}, got {reader.TokenType}");
		}

		var result = new ObjectData();
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				return result;
			}
			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				throw new JsonException($"Expected {JsonTokenType.PropertyName}, got {reader.TokenType}");
			}

			var name = reader.GetString() ?? throw new JsonException("ObjectData property name was null");
			if (!reader.Read())
			{
				throw new JsonException("Unexpected end of ObjectData JSON");
			}
			result[name] = JsonSerializer.Deserialize<object?>(ref reader, options);
		}

		throw new JsonException("Unexpected end of ObjectData JSON");
	}

	public override void Write(Utf8JsonWriter writer, ObjectData value, JsonSerializerOptions options)
	{
		writer.WriteStartObject();
		foreach (var kvp in value)
		{
			writer.WritePropertyName(kvp.Key);
			JsonSerializer.Serialize(writer, kvp.Value, options);
		}
		writer.WriteEndObject();
	}
}
