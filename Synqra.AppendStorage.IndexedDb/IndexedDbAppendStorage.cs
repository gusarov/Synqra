using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Synqra.BinarySerializer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
// using TG.Blazor.IndexedDB;

namespace Synqra.AppendStorage.IndexedDb;

// IndexedDB Guide: https://developer.mozilla.org/en-US/docs/Web/API/IndexedDB_API/Using_IndexedDB
// Blazor School: https://blazorschool.com/tutorial/blazor-wasm/dotnet6/indexeddb-storage-749869
// TGNuget: https://github.com/wtulloch/Blazor.IndexedDB
// SpawnDev.BlazorJS: https://github.com/LostBeard/SpawnDev.BlazorJS/blob/main/SpawnDev.BlazorJS/wwwroot/SpawnDev.BlazorJS.lib.module.js

// [SupportedRuntime]

internal record struct EventItem<TKey>
{
	public TKey Key { get; init; }
	public byte[] Bin { get; init; }
#if DEBUG
	public object Debug { get; init; }
#endif
}

internal record struct EventItem
{
	public byte[] Bin { get; init; }
#if DEBUG
	public object Debug { get; init; }
#endif
}

internal class IndexedDbAppendStorage<T, TKey> : IAppendStorage<T, TKey>
		where T : class
{
	private readonly Func<T, TKey> _keyAccessor;
	private readonly IndexedDbJsInterop _indexedDbInterop;
	private readonly ISBXSerializerFactory _sbxSerializerFactory;
	private readonly ISBXSerializer _sbxSerializer;

	// private readonly IndexedDBManager _tgnDbManager;

	public IndexedDbAppendStorage(Func<T, TKey> keyAccessor, IndexedDbJsInterop jsInterop, ISBXSerializerFactory sbxSerializerFactory)
	{
		_keyAccessor = keyAccessor;
		_indexedDbInterop = jsInterop;
		_sbxSerializerFactory = sbxSerializerFactory;
		_sbxSerializer = _sbxSerializerFactory.CreateSerializer();
		_ = _indexedDbInterop.InitializeAsync();
		// _tgnDbManager = tgnDbManager;
	}

	public async Task AppendAsync(T item, CancellationToken cancellationToken = default)
	{
		if (item != null)
		{
			Span<byte> buf = stackalloc byte[16 * 1024];
			int pos = 0;
			_sbxSerializer.Reset();
			_sbxSerializer.Serialize(buf, item, ref pos); // to isolate sideeffects for now, every item is with fresh serializer state. This needs to be changed, but this likely requires special event to be logged that resets serializer state.

			await _indexedDbInterop.AddAsync(new EventItem
			{
				Bin = buf[..pos].ToArray(),
#if DEBUG
				Debug = item,
#endif
			}, _keyAccessor(item));
			// Console.WriteLine(await _indexedDbInterop.TestAsync($"AppendAsync: {item}"));
		}
	}
	public async Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
	{
		var list = new List<EventItem<TKey>>();
		foreach (var item in items)
		{
			Span<byte> buf = stackalloc byte[16 * 1024];
			int pos = 0;
			_sbxSerializer.Reset();
			_sbxSerializer.Serialize(buf, item, ref pos); // to isolate sideeffects for now, every item is with fresh serializer state. This needs to be changed, but this likely requires special event to be logged that resets serializer state.

			EventItem<TKey> itemDto = new EventItem<TKey>
			{
				Key = _keyAccessor(item),
				Bin = buf[..pos].ToArray(),
#if DEBUG
				// Debug = item,
#endif
			};
			list.Add(itemDto);
		}

		await _indexedDbInterop.AddBatchAsync(list, "key");
	}

	public Task<string> TestAsync(string input)
	{
		return _indexedDbInterop.TestAsync(input);
	}

	public void Dispose()
	{
	}

	public async ValueTask DisposeAsync()
	{
	}

	public async IAsyncEnumerable<T> GetAllAsync(TKey? fromExcluding = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		bool any = false;
		T lastItem = default!;
		int pageSize = 1024;
		do
		{
			var sw = Stopwatch.StartNew();
			var page = await _indexedDbInterop.GetAllAsync<EventItem, TKey>(fromExcluding, pageSize);
			if (sw.ElapsedMilliseconds < 300)
			{
				pageSize = (int)(pageSize * 300.0 / sw.ElapsedMilliseconds); // page should be big enough to fill the 300ms target
			}
			any = false;
			foreach (var item in page)
			{
				Span<byte> buf = item.Bin.AsSpan();
				int pos = 0;
				_sbxSerializer.Reset();
				T element;
				try
				{
					element = _sbxSerializer.Deserialize<T>(buf, ref pos); // to isolate sideeffects for now, every item is with fresh serializer state. This needs to be changed, but this likely requires special event to be logged that resets serializer state.
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"{ex}");
					throw;
				}
				yield return element;
				lastItem = element;
				any = true;
			}
			if (any)
			{
				fromExcluding = _keyAccessor(lastItem);
			}
		} while (any);
	}

	public Task<T> GetAsync(TKey key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}
}
