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

	// IHostEnvironment is optional: a Blazor WebAssemblyHostBuilder app never registers
	// it at all (it has IWebAssemblyHostEnvironment instead) — a required parameter here
	// would fail DI construction outright on a real browser client before ever reaching
	// the browser-check below. No filesystem there either way (unlike the "WASM-like"
	// simulator nodes used in tests, which are real .NET processes) — File.WriteAllText
	// would throw. Falls back to in-memory-only: a fresh node id and replication cursor
	// each page load. Correctness is unaffected — the server already dedups by EventId —
	// this only means a reload can't resume exactly where it left off and may briefly
	// re-send already-known events, which the server's own dedup absorbs.
	public EventReplicationState(IHostEnvironment? hostEnvironment = null)
	{
		if (hostEnvironment is null || RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
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