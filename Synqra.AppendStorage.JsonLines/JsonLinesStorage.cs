#define LOCK
// #define SEMAPHORE

using Microsoft.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Synqra.AppendStorage.JsonLines;

public static class AppendStorageJsonLinesExtensions
{
	static object _synqraJsonLinesStorageConfiguredKey = new object();

	public static void AddAppendStorageJsonLines<T>(this IHostApplicationBuilder hostBuilder, string keyFieldName, Func<T, (Guid, Guid)> getKey)
	where T : class
	{
		AddAppendStorageJsonLines(hostBuilder, keyFieldName, getKey, x => x.Item1.ToString("N") + x.Item2.ToString("N"), path => (Guid.Parse(path.Replace(Path.DirectorySeparatorChar + "", "")[..32]), Guid.Parse(path.Replace(Path.DirectorySeparatorChar + "", "")[32..])));
	}

	public static void AddAppendStorageJsonLines<T>(this IHostApplicationBuilder hostBuilder, string keyFieldName, Func<T, Guid> getKey)
		where T : class
	{
		AddAppendStorageJsonLines(hostBuilder, keyFieldName, getKey, x => x.ToString("N"), Guid.Parse);
	}

	// For nativeAOT
	public static void AddAppendStorageJsonLines<T, TKey>(this IHostApplicationBuilder hostBuilder
		, string keyFieldName
		, Func<T, TKey> getKey
		, Func<TKey, string> getKeyHex
		, Func<string, TKey> getHexKey
		)
		where T : class
		// where T : IIdentifiable<TKey>
	{
		// _ = typeof(IStorage<T, TKey>);
		// _ = typeof(JsonLinesStorage<T, TKey>);
		hostBuilder.AddAppendStorageJsonLinesCore();
		/*
		hostBuilder.Services.Configure<JsonLinesStorageConfigInstance>(typeof(T).Name, x =>
		{
			x.ObjectIdFieldName = keyFieldName;
		});
		*/
		hostBuilder.Services.TryAddSingleton<IAppendStorage<T, TKey>, JsonLinesStorage<T, TKey>>();
		hostBuilder.Services.AddSingleton(getKey);
		hostBuilder.Services.AddSingleton(getKeyHex);
		hostBuilder.Services.AddSingleton(getHexKey);
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

	internal class JsonLinesStorageConfigInstance
	{
		public string ObjectIdFieldName { get; set; }
	}

	private class JsonLinesStorage<T, TKey> : IAppendStorage<T, TKey>, IDisposable, IAsyncDisposable
		where T : class
		//where T : IIdentifiable<TKey>
	{
		private readonly ILogger _logger;
		private readonly IOptions<JsonLinesStorageConfig> _options;
		private readonly string _keyFieldName;
#if LOCK
		private readonly object _lock = new object();
#elif SEMAPHORE
		private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
#endif
		private FileStream? _stream;
		private StreamWriter? _streamWriter;
		private JsonSerializerOptions _serializerOptions;
		private readonly ConcurrentDictionary<TKey, WeakReference> _attachedObjectsById = new();
		private readonly Func<T, TKey> _getKeyFromItem;
		private readonly Func<TKey, string> _getPathFromKey;
		private readonly Func<string, TKey> _getKeyFromPath;

		public JsonLinesStorage(
			  ILogger<JsonLinesStorage<T, TKey>> logger
			, IOptions<JsonLinesStorageConfig> options
			, IOptionsFactory<JsonLinesStorageConfigInstance> instanceOptions
			, Func<T, TKey> getKeyFromItem
			, Func<TKey, string> getPathFromKey
			, Func<string, TKey> getKeyFromPath
			, JsonSerializerOptions? jsonSerializerOptions = null
			)
		{
			_logger = logger;

			_options = options;
			_keyFieldName = instanceOptions.Create(typeof(T).Name).ObjectIdFieldName;
			_getKeyFromItem = getKeyFromItem;
			_getPathFromKey = getPathFromKey;
			_getKeyFromPath = getKeyFromPath;

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
					_fileName = _options.Value.FileName.Replace("[Type]", typeof(T).Name).Replace("[TypeName]", typeof(T).Name);
				}
				return _fileName;
			}
		}

		private static Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

		private void EventuallyMaintain()
		{
			if (Random.Shared.Next(1024) == 0)
			{
				foreach (var deadKey in _attachedObjectsById.Where(x => !x.Value.IsAlive).Select(x => x.Key))
				{
					_attachedObjectsById.TryRemove(deadKey, out _);
				}
			}
		}

		public async IAsyncEnumerable<T> GetAllAsync(TKey? from = default, CancellationToken cancellationToken = default)
		{
			string? _currentLine;
			StreamReader? _streamReader;

			if (File.Exists(FileName))
			{
				var stream = new FileStream(
					  FileName
					, FileMode.Open
					, FileAccess.Read
					, FileShare.ReadWrite
					, bufferSize: 1024 * 64
					, FileOptions.SequentialScan | FileOptions.Asynchronous
					);
				_streamReader = new StreamReader(stream, encoding: _utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024 * 64);
			}
			else
			{
				yield break;
			}

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

			var _fromStr = _getPathFromKey(from);
			if (_fromStr.Length == 64 && _fromStr.TrimEnd('0').Length <= 32)
			{
				_fromStr = _fromStr[..32];
			}

			while (!_streamReader.EndOfStream)
			{
				_currentLine = await _streamReader.ReadLineAsync();

				var parts = _currentLine.Split(new[] { '§' }, 2);
				var (keyStr, json) = (parts[0], parts[1]);
				TKey key = _getKeyFromPath(keyStr);

				if (!Equals(from, default(TKey)))
				{
					if (!keyStr.StartsWith(_fromStr))
					{
						continue;
					}
				}

				bool hit = false;
				EventuallyMaintain();
				if (_attachedObjectsById.TryGetValue(key, out var weakRef))
				{
					var target = weakRef.Target;
					if (target != null)
					{
						yield return (T)target;
						hit = true;
					}
				}
				if (!hit)
				{
					var obj = JsonSerializer.Deserialize<T>(json, _serializerOptions)!;
					_attachedObjectsById[key] = new WeakReference(obj);
					yield return obj;
				}

			}

			// return new JsonLinesAsyncEnumerable(this, from, cancellationToken);
		}

		private class JsonLinesAsyncEnumerable : IAsyncEnumerable<T>
		{
			private readonly JsonLinesStorage<T, TKey> _storage;
			private readonly TKey? _from;
			private readonly CancellationToken _cancellationToken;

			public JsonLinesAsyncEnumerable(JsonLinesStorage<T, TKey> storage, TKey? from = default, CancellationToken cancellationToken = default)
			{
				_storage = storage;
				_from = from;
				_cancellationToken = cancellationToken;
			}

			public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
			{
				if (cancellationToken == default)
				{
					cancellationToken = _cancellationToken;
				}
				return new JsonLinesAsyncEnumerator(_storage, _from, _storage.FileName, _storage._serializerOptions, cancellationToken);
			}
		}

		private class JsonLinesAsyncEnumerator : IAsyncEnumerator<T>
		{
			private static Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
			private readonly JsonLinesStorage<T, TKey> _storage;
			private readonly TKey? _from;
			private readonly string _fromStr;
			private readonly string _fileName;
			private readonly JsonSerializerOptions _jsonSerializerOptions;
			private readonly CancellationToken? _cancellationToken;

			private int _state;
			private string? _currentLine;
			private StreamReader? _streamReader;

			public JsonLinesAsyncEnumerator(JsonLinesStorage<T, TKey> storage, TKey? from, string fileName, JsonSerializerOptions jsonSerializerOptions, CancellationToken? cancellationToken = default)
			{
				_storage = storage;
				_from = from;
				_fromStr = _storage._getPathFromKey(from);
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
					var parts = _currentLine.Split(new[] { '§' }, 2);
					var (keyStr, json) = (parts[0], parts[1]);
					TKey key = _storage._getKeyFromPath(keyStr);

					/*
					if (!Equals(_from, default))
					{
						if (keyStr.StartsWith(_fromStr))
						{
							ContextBoundObject
						}
					}
					*/
					_storage.EventuallyMaintain();
					if (_storage._attachedObjectsById.TryGetValue(key, out var weakRef))
					{
						var target = weakRef.Target;
						if (target != null)
						{
							return (T)target;
						}
					}
					var obj = JsonSerializer.Deserialize<T>(json, _jsonSerializerOptions)!;
					_storage._attachedObjectsById[key] = new WeakReference(obj);
					return obj;
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

			var key = _getKeyFromItem(item);
			bool potentialReplace = false;
			EventuallyMaintain();
			if (!_attachedObjectsById.TryAdd(key, new WeakReference(item)))
			{
				// This mean - just write same object over again
				potentialReplace = true;
				// throw new Exception("Object already tracked by AppendStorage, it is not new!");
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
					Directory.CreateDirectory(Path.GetDirectoryName(FileName));
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
					 
					if (_stream.Length == 0) // Header
					{
						var header = JsonSerializer.Serialize(new JsonLinesStorageHeader
						{
							Version = new Version(0, 1),
							RootItemType = typeof(T).FullName,
						}, JsonLinesStorageInternalSerializerContext.Default.JsonLinesStorageHeader);
						_streamWriter.WriteLine(header);
						_streamWriter.Flush();
					}
				}
#if LOCK
				var keyStr = _getPathFromKey(key);

				#region Delete old record

				using var streamR = new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write, bufferSize: 1024 * 64, options: FileOptions.SequentialScan);

				long lineStart = 0;
				var lineBytes = new MemoryStream();
				int b;
				while ((b = streamR.ReadByte()) != -1)
				{
					if (b == '\n')
					{
						var lineData = lineBytes.ToArray();
						int len = lineData.Length;
						if (len > 0 && lineData[len - 1] == (byte)'\r') len--;

						var line = _utf8NoBom.GetString(lineData, 0, len);
						var parts = line.Split(new[] { '§' }, 2);
						if (parts.Length >= 2)
						{
							var keyStrR = parts[0];
							if (keyStrR == keyStr)
							{
								var orig = _stream.Position;
								_stream.Seek(lineStart, SeekOrigin.Begin);
								var zeroBytes = _utf8NoBom.GetBytes(new string('0', keyStrR.Length));
								_stream.Write(zeroBytes, 0, zeroBytes.Length);
								_stream.Flush();
								_stream.Seek(orig, SeekOrigin.Begin);
								break;
							}
						}
						lineBytes.SetLength(0);
						lineStart = streamR.Position;
					}
					else
					{
						lineBytes.WriteByte((byte)b);
					}
				}

				#endregion

				_streamWriter.WriteLine($"{keyStr}§{json}");

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

		public async Task<T> GetAsync(TKey key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			// in this storage we can only get all items, so we will read all and find the one with matching key
			var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
			using var stream = new FileStream(
				  FileName
				, FileMode.Open
				, FileAccess.Read
				, FileShare.ReadWrite
				, bufferSize: 1024 * 64
				, FileOptions.SequentialScan | FileOptions.Asynchronous
				);
			using var reader = new StreamReader(stream, encoding: utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024 * 64);

			// Skip header
			var headerLine = await reader.ReadLineAsync(cancellationToken);
			if (headerLine == null)
			{
				throw new InvalidOperationException("Header line of Syncra.Storage.Jsonl is not found");
			}

			var keyJson = JsonSerializer.Serialize(key, _serializerOptions);

			EventuallyMaintain();

			string? line;
			while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var parts = line.Split(new[] { '§' }, 2);
				var (keyStr, json) = (parts[0], parts[1]);
				var thisKey = _getKeyFromPath(keyStr);

				if (Equals(thisKey, key))
				{
					if (_attachedObjectsById.TryGetValue(key, out var weakRef))
					{
						var target = weakRef.Target;
						if (target != null)
						{
							return (T)target;
						}
					}
					var obj = JsonSerializer.Deserialize<T>(json, _serializerOptions)!;
					_attachedObjectsById[key] = new WeakReference(obj);
					return obj;
				}
			}

			throw new KeyNotFoundException($"Item with key '{key}' was not found");
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
