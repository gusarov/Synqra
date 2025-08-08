#define LOCK
//#define SEMAPHORE

using Microsoft.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Synqra.Storage;

public static class StorageExtensions
{
	public static void AddJsonLinesStorage(this IHostApplicationBuilder hostBuilder)
	{
		hostBuilder.Services.AddSingleton<IStorage, JsonLinesStorage>();
		hostBuilder.Services.Configure<JsonLinesStorageConfig>(hostBuilder.Configuration.GetSection("JsonLinesStorage"));
	}

	private class JsonLinesStorageConfig
	{
		public string FileName { get; set; } = "[TypeName].jsonl";
	}

	private class JsonLinesStorage : IStorage, IDisposable, IAsyncDisposable
	{
		private readonly ILogger _logger;
		private readonly IOptions<JsonLinesStorageConfig> _options;
#if LOCK
		private readonly object _lock = new object();
#elif SEMAPHORE
		private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
#endif
		FileStream? _stream;
		StreamWriter? _streamWriter;
		JsonSerializerOptions _serializerOptions;

		public JsonLinesStorage(ILogger<JsonLinesStorage> logger, IOptions<JsonLinesStorageConfig> options, JsonSerializerOptions? jsonSerializerOptions = null)
		{
			_logger = logger;

			_options = options;

			if (!JsonSerializer.IsReflectionEnabledByDefault && jsonSerializerOptions is null)
			{
				_logger.LogWarning("jsonSerializerOptions might be required in DI for NativeAOT environment");
				throw new ArgumentNullException(nameof(jsonSerializerOptions), "jsonSerializerOptions might be required in DI for NativeAOT environment");
			}
			_serializerOptions = jsonSerializerOptions is null
				? new JsonSerializerOptions()
				: new JsonSerializerOptions(jsonSerializerOptions);
			_serializerOptions.WriteIndented = false; // critical for jsonl
		}

		Type? _itemType;
		string? _fileName;
		string FileName
		{
			get
			{
				if (_fileName == null)
				{
					_fileName = _options.Value.FileName.Replace("[TypeName]", _itemType?.Name ?? "data", StringComparison.OrdinalIgnoreCase);
				}
				return _fileName;
			}
		}

		public async IAsyncEnumerable<T> GetAll<T>()
		{
			if (_stream != null)
			{
				throw new Exception("File is currently appending, you can not read it again"); // this exception is more specific to event stores. Before removing this limitation, consider how you going to read incomplete line of non flushed data
			}
			if (_itemType is null)
			{
				_itemType = typeof(T);
			}
			else
			{
				if (_itemType != typeof(T))
				{
					throw new Exception($"Item type mismatch, expected '{_itemType.FullName}' but got '{typeof(T).FullName}'");
				}
			}

			if (File.Exists(FileName))
			{
				// xxx file for writes and read it all to the end
				using var stream = new FileStream(FileName, FileMode.OpenOrCreate /* already checked that it exists, so this is just to avoid error if it is been deleted in fraction of ms */, FileAccess.Read, FileShare.Read /* Do not allow parallel write! */, bufferSize: 1024 * 64, FileOptions.SequentialScan | FileOptions.Asynchronous);
				using var streamReader = new StreamReader(stream, encoding: Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024 * 64);
				string? line;

				#region Header check

				line = await streamReader.ReadLineAsync();
				if (line == null)
				{
					throw new Exception("Header line of Syncra.Storage.Jsonl is not found");
				}
				var header = JsonSerializer.Deserialize(line, JsonLinesStorageInternalSerializerContext.Default.JsonLinesStorageHeader);
				if (header == null || header.Version == null)
				{
					throw new Exception("Header line of Syncra.Storage.Jsonl is not valid");
				}
				if (header.Version != "1.0.0")
				{
					throw new Exception("File version is newer than expected");
				}

				#endregion

				while ((line = await streamReader.ReadLineAsync()) != null)
				{
					if (line.StartsWith("{"))
					{
						yield return JsonSerializer.Deserialize<T>(line, _serializerOptions)!;
					}
					else
					{
						throw new Exception($"Wrong line format '{line}'");
						_logger.LogWarning($"Loading - skipped line '{line}'");
					}
				}
			}
		}

		public async Task AppendAsync<T>(T item)
		{
			if (item == null)
			{
				throw new ArgumentNullException(nameof(item), "Item to append can not be null");
			}
			if (_itemType is null)
			{
				_itemType = typeof(T);
			}
			else
			{
				if (_itemType != typeof(T))
				{
					throw new Exception($"Item type mismatch, expected '{_itemType.FullName}' but got '{typeof(T).FullName}'");
				}
			}

			// first searialize then open
			// 1. because it might fail to searialize but you created empty file
			// 2. because we can write first data line under same lock
			var json = JsonSerializer.Serialize(item, _serializerOptions);

#if LOCK
			lock (_lock)
			{
#elif SEMAPHORE
			await _semaphore.WaitAsync();
			try
			{
#endif
				if (_streamWriter == null)
				{
					_stream = new FileStream(FileName, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1024 * 4, options: FileOptions.SequentialScan
#if SEMAPHORE
| FileOptions.Asynchronous
#endif
						);
					_streamWriter = new StreamWriter(_stream, new UTF8Encoding(false, false), bufferSize: 1024 * 64);
					// Header
					var header = JsonSerializer.Serialize(new JsonLinesStorageHeader
					{
						Version = "1.0.0",
						ItemType = item.GetType().FullName,
					}, JsonLinesStorageInternalSerializerContext.Default.JsonLinesStorageHeader);
					_streamWriter.WriteLine(header);
				}
				_streamWriter.WriteLine(json);
				// await _streamWriter.WriteLineAsync(json);
#if LOCK
			}
#elif SEMAPHORE
			}
			finally
			{
				_semaphore.Release();
			}
#endif
		}

		public void Dispose()
		{
#if LOCK
			lock (_lock)
			{
				_streamWriter?.Dispose();
				_stream?.Dispose();
				_streamWriter = null;
				_stream = null;
			}
#elif SEMAPHORE
			_semaphore.Wait();
			try
			{
				if (_streamWriter != null)
				{
					_streamWriter?.Dispose();
					_streamWriter = null;
				}
				if (_stream != null)
				{
					_stream?.Dispose();
					_stream = null;
				}
			}
			finally
			{
				_semaphore.Release();
			}
#endif
		}

		public async ValueTask DisposeAsync()
		{
#if LOCK
			Dispose();
#elif SEMAPHORE
			await _semaphore.WaitAsync();
			try
			{

				var sw = _streamWriter;
				if (sw != null)
				{
					await sw.DisposeAsync();
				}
				_streamWriter = null;

				/* It is not marked as leaveOpen so writer will close the stream
				var s = _stream;
				if (s != null)
				{
					await s.DisposeAsync();
					_stream = null;
				}
				*/
				_stream = null;
			}
			finally
			{
				_semaphore.Release();
			}
#endif
		}

		public async Task FlushAsync()
		{
#if LOCK
			lock (_lock)
			{
				_streamWriter?.Flush();
			}
#elif SEMAPHORE
			await _semaphore.WaitAsync();
			try
			{
				var sw = _streamWriter;
				if (sw != null)
				{
					await sw.FlushAsync();
				}
			}
			finally
			{
				_semaphore.Release();
			}
#endif
		}
	}

}

class JsonLinesStorageHeader
{
	[JsonPropertyName("Synqra.Storage.Jsonl")]
	public string? Version { get; set; }
	public string? ItemType { get; set; }
}

[JsonSourceGenerationOptions(
	  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
	, DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase
	, WriteIndented = false
	, GenerationMode = JsonSourceGenerationMode.Default
	, DefaultBufferSize = 16384
	, IgnoreReadOnlyFields = false
	, IgnoreReadOnlyProperties = false
	, IncludeFields = false
	, AllowTrailingCommas = true
	, ReadCommentHandling = JsonCommentHandling.Skip
)]
[JsonSerializable(typeof(JsonLinesStorageHeader))]
partial class JsonLinesStorageInternalSerializerContext : JsonSerializerContext
{
}