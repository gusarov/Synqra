using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Synqra;

// Persistent state with metadata about replication, e.g. known version vectors, node ids
public class EventReplicationState
{
	string _fileName;

	public EventReplicationState(IHostEnvironment hostEnvironment)
	{
		_fileName = Path.Combine(hostEnvironment.ContentRootPath, "EventReplicationState.json");

		if (File.Exists(_fileName))
		{
			this.RSetSTJ(File.ReadAllText(_fileName), EventReplicationStateJsonSerializerContext.Default);
		}
		else
		{
			MyNodeId = Guid.NewGuid();
			Save();
		}
	}

	public void Save()
	{
		File.WriteAllText(_fileName, JsonSerializer.Serialize(this, EventReplicationStateJsonSerializerContext.Default.Options));
	}

	public Guid MyNodeId { get; set; }
	public Guid LastEventIdFromMe { get; set; }
	public Guid LastEventIdFromServer { get; set; }
}

[JsonSourceGenerationOptions(
	  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
	, DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase
	, GenerationMode = JsonSourceGenerationMode.Default
	, DefaultBufferSize = 16384
	, IgnoreReadOnlyFields = false
	, IgnoreReadOnlyProperties = false
	, IncludeFields = false
	, AllowTrailingCommas = true
// , ReadCommentHandling = JsonCommentHandling.Skip
#if DEBUG
	, IndentCharacter = '\t'
	, IndentSize = 1
	, WriteIndented = true
#endif
)]
[JsonSerializable(typeof(EventReplicationState))]
internal partial class EventReplicationStateJsonSerializerContext : JsonSerializerContext
{
}