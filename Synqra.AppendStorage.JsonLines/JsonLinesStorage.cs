#define LOCK
// #define SEMAPHORE

using Microsoft.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Synqra.AppendStorage.JsonLines;

public static class AppendStorageJsonLinesExtensions
{
	static object _synqraJsonLinesStorageConfiguredKey = new object();

	// For nativeAOT
	public static void AddAppendStorageJsonLines<T, TKey>(this IHostApplicationBuilder hostBuilder)
		// where T : IIdentifiable<TKey>
	{
		// _ = typeof(IStorage<T, TKey>);
		// _ = typeof(JsonLinesStorage<T, TKey>);
		hostBuilder.AddAppendStorageJsonLinesCore();
		hostBuilder.Services.TryAddSingleton<IAppendStorage<T, TKey>, JsonLinesStorage<T, TKey>>();
	}

	public static void AddAppendStorageJsonLines(this IHostApplicationBuilder hostBuilder)
	{
		hostBuilder.AddAppendStorageJsonLinesCore();
		hostBuilder.Services.TryAddSingleton(typeof(IAppendStorage<,>), typeof(JsonLinesStorage<,>));
	}

	internal static void AddAppendStorageJsonLinesCore(this IHostApplicationBuilder hostBuilder)
	{
		if (hostBuilder.Properties.TryAdd(_synqraJsonLinesStorageConfiguredKey, string.Empty))
		{
			hostBuilder.Services.Configure<JsonLinesStorageConfig>(hostBuilder.Configuration.GetSection("Storage:JsonLinesStorage"));
		}
	}

	internal class JsonLinesStorageConfig // it is internal only because of AOT bindings
	{
		public string FileName { get; set; } = "[TypeName].jsonl";
	}

	private class JsonLinesStorage<T, TKey> : IAppendStorage<T, TKey>, IDisposable, IAsyncDisposable
		//where T : IIdentifiable<TKey>
	{
		private readonly ILogger _logger;
		private readonly IOptions<JsonLinesStorageConfig> _options;
#if LOCK
		private readonly object _lock = new object();
#elif SEMAPHORE
		private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
#endif
		private FileStream? _stream;
		private StreamWriter? _streamWriter;
		private JsonSerializerOptions _serializerOptions;

		public JsonLinesStorage(
			  ILogger<JsonLinesStorage<T, TKey>> logger
			, IOptions<JsonLinesStorageConfig> options
			, JsonSerializerOptions? jsonSerializerOptions = null
			)
		{
			_logger = logger;

			_options = options;

			/*
			if (!JsonSerializer.IsReflectionEnabledByDefault && jsonSerializerOptions is null)
			{
				_logger.LogWarning("jsonSerializerOptions might be required in DI for NativeAOT environment");
				throw new ArgumentNullException(nameof(jsonSerializerOptions), "jsonSerializerOptions might be required in DI for NativeAOT environment");
			}
			*/
			_serializerOptions = jsonSerializerOptions is null
				? throw new ArgumentNullException(nameof(jsonSerializerOptions))
				: new JsonSerializerOptions(jsonSerializerOptions);
			_serializerOptions.WriteIndented = false; // critical for jsonl
		}

		private string? _fileName;

		private string FileName
		{
			get
			{
				if (_fileName == null)
				{
					_fileName = _options.Value.FileName.Replace("[TypeName]", typeof(T).Name);
				}
				return _fileName;
			}
		}

		public IAsyncEnumerable<T> GetAllAsync(TKey? from = default, CancellationToken cancellationToken = default)
		{
			return new JsonLinesAsyncEnumerable(this, cancellationToken);
		}

		private class JsonLinesAsyncEnumerable : IAsyncEnumerable<T>
		{
			private readonly JsonLinesStorage<T, TKey> _storage;
			private readonly CancellationToken _cancellationToken;

			public JsonLinesAsyncEnumerable(JsonLinesStorage<T, TKey> storage, CancellationToken cancellationToken)
			{
				_storage = storage;
				_cancellationToken = cancellationToken;
			}

			public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
			{
				if (cancellationToken == default)
				{
					cancellationToken = _cancellationToken;
				}
				return new JsonLinesAsyncEnumerator(_storage.FileName, _storage._serializerOptions, cancellationToken);
			}
		}

		private class JsonLinesAsyncEnumerator : IAsyncEnumerator<T>
		{
			private static Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

			private readonly string _fileName;
			private readonly JsonSerializerOptions _jsonSerializerOptions;
			private readonly CancellationToken? _cancellationToken;

			private int _state;
			private string? _currentLine;
			private StreamReader? _streamReader;

			public JsonLinesAsyncEnumerator(string fileName, JsonSerializerOptions jsonSerializerOptions, CancellationToken? cancellationToken = default)
			{
				_fileName = fileName;
				_jsonSerializerOptions = jsonSerializerOptions;
				_cancellationToken = cancellationToken;
			}

			public T Current
			{
				get
				{
					if (_currentLine == null)
					{
						throw new InvalidOperationException("No current line, did you call MoveNextAsync?");
					}
					return JsonSerializer.Deserialize<T>(_currentLine, _jsonSerializerOptions)!;
				}
			}

			public ValueTask DisposeAsync()
			{
				_streamReader?.Dispose();
				return default;
			}

			public async ValueTask<bool> MoveNextAsync()
			{
				if (_state == 0)
				{
					if (File.Exists(_fileName))
					{
						var stream = new FileStream(
							  _fileName
							, FileMode.Open
							, FileAccess.Read
							, FileShare.ReadWrite
							, bufferSize: 1024 * 64
							, FileOptions.SequentialScan | FileOptions.Asynchronous
							);
						_streamReader = new StreamReader(stream, encoding: _utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024 * 64);
						_state = 1;
					}
					else
					{
						return false;
					}

				}

				#region Header check

				if (_state == 1)
				{
					var lineHeader = await _streamReader.ReadLineAsync();
					if (lineHeader == null)
					{
						throw new Exception("Header line of Syncra.Storage.Jsonl is not found");
					}
					var header = JsonSerializer.Deserialize(lineHeader, JsonLinesStorageInternalSerializerContext.Default.JsonLinesStorageHeader);
					if (header == null || header.Version == null)
					{
						throw new Exception("Header line of Syncra.Storage.Jsonl is not valid");
					}
					if (header.Version != new Version("0.1"))
					{
						throw new Exception("File version is newer than expected");
					}
					_state = 2;
				}

				#endregion

				_currentLine = await _streamReader.ReadLineAsync();
				return _currentLine != null;
			}
		}

		public async Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
		{
			// TODO: optimize this by writing batch instead of line by line
			foreach (var item in items)
			{
				await AppendAsync(item, cancellationToken);
			}
		}

		public async Task AppendAsync(T item, CancellationToken cancellationToken = default)
		{
			if (item == null)
			{
				throw new ArgumentNullException(nameof(item), "Item to append can not be null");
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
					_streamWriter = new StreamWriter(_stream)
					{
						AutoFlush = true, // today there is no other way to synchronize reader and writer
#if DEBUG
#endif
					};// , new UTF8Encoding(false, false), bufferSize: 1024 * 64);
					// Header
					var header = JsonSerializer.Serialize(new JsonLinesStorageHeader
					{
						Version = new Version(0, 1),
						RootItemType = typeof(T).FullName,
					}, JsonLinesStorageInternalSerializerContext.Default.JsonLinesStorageHeader);
					_streamWriter.WriteLine(header);
					_streamWriter.Flush();
				}
#if LOCK
				_streamWriter.WriteLine(json);
#elif SEMAPHORE
				await _streamWriter.WriteLineAsync(json);
#endif
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
			GC.SuppressFinalize(this);
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
			GC.SuppressFinalize(this);
#if LOCK
			Dispose();
#elif SEMAPHORE
			await _semaphore.WaitAsync();
			try
			{

				var sw = _streamWriter;
				if (sw != null)
				{
#if NETSTANDARD2_0
					sw.Dispose();
#else
					await sw.DisposeAsync();
#endif
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

		public async Task FlushAsync(CancellationToken cancellationToken = default)
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

		public Task<string> TestAsync(string input)
		{
			throw new NotImplementedException();
		}

		~JsonLinesStorage()
		{
			Dispose();
		}
	}
}

class JsonLinesStorageHeader
{
	[JsonPropertyName("Synqra.Storage.Jsonl")]
	public Version? Version { get; set; } // do not assign default!! You need to read it from actual header!
	public string? RootItemType { get; set; }
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
