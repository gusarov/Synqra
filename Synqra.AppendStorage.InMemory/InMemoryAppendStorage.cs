using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Runtime.CompilerServices;

namespace Synqra.AppendStorage.InMemory;

public static class InMemoryAppendStorageExtensions
{
	public static IHostApplicationBuilder AddInMemoryAppendStorage<T, TKey>(this IHostApplicationBuilder hostBuilder, Func<T, TKey> getKey)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		hostBuilder.Services.AddInMemoryAppendStorage(getKey);
		return hostBuilder;
	}

	public static IServiceCollection AddInMemoryAppendStorage<T, TKey>(this IServiceCollection services, Func<T, TKey> getKey)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		services.TryAddKeyedSingleton<Func<T, TKey>>("Synqra.AppendStorage.InMemory", getKey);
		services.TryAddSingleton<IAppendStorage<T, TKey>, InMemoryAppendStorage<T, TKey>>();
		return services;
	}

	private class InMemoryAppendStorage<T, TKey> : IAppendStorage<T, TKey>
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		private readonly Func<T, TKey> _getKey;
		private SortedDictionary<TKey, T> _data = new SortedDictionary<TKey, T>();

		public InMemoryAppendStorage([FromKeyedServices("Synqra.AppendStorage.InMemory")] Func<T, TKey> getKey)
		{
			_getKey = getKey;
		}

		public Task AppendAsync(T item, CancellationToken cancellationToken = default)
		{
			lock (_data)
			{
				_data.Add(_getKey(item), item);
			}
			return Task.CompletedTask;
		}

		public void Dispose()
		{
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}

		public async IAsyncEnumerable<T> GetAllAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			KeyValuePair<TKey, T>[] data;
			lock (_data)
			{
				data = _data.ToArray();
			}
			foreach (var item in data.SkipWhile(x => x.Key.CompareTo(from ?? default(TKey)) < 0))
			{
				yield return item.Value;
			}
		}

		public Task<T> GetAsync(TKey key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			return Task.FromResult(_data[key]);
		}
	}
}
