using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Synqra;

// Persistent state with metadata about replication, e.g. known version vectors, node ids
public class EventReplicationState
{
	readonly string? _fileName;

	public EventReplicationState(IHostEnvironment hostEnvironment)
	{
		// No filesystem in a real browser WASM sandbox (unlike the "WASM-like" simulator
		// nodes used in tests, which are real .NET processes) — File.WriteAllText there
		// throws. Falls back to in-memory-only: a fresh node id and replication cursor
		// each page load. Correctness is unaffected — the server already dedups by
		// EventId — this only means a reload can't resume exactly where it left off and
		// may briefly re-send already-known events, which the server's own dedup absorbs.
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
		{
			_fileName = null;
			MyNodeId = Guid.NewGuid();
			return;
		}

		_fileName = Path.Combine(hostEnvironment.ContentRootPath, "EventReplicationState.json");

		if (File.Exists(_fileName))
		{
			this.RSetSTJ(File.ReadAllText(_fileName), EventReplicationStateJsonSerializerContext.Default.Options);
		}
		else
		{
			MyNodeId = Guid.NewGuid();
			Save();
		}
	}

	public void Save()
	{
		if (_fileName is null)
		{
			return;
		}
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