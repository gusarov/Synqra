using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Runtime.CompilerServices;

namespace Synqra.AppendStorage.InMemory;

public static class InMemoryAppendStorageExtensions
{
	public static IHostApplicationBuilder AddAppendStorageInMemory<T, TKey>(this IHostApplicationBuilder hostBuilder, Func<T, TKey> getKey)
		where T : class
		where TKey : notnull, IComparable<TKey>
	{
		hostBuilder.Services.AddAppendStorageInMemory(getKey);
		return hostBuilder;
	}

	public static IServiceCollection AddAppendStorageInMemory<T, TKey>(this IServiceCollection services, Func<T, TKey> getKey)
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
		private readonly SortedDictionary<TKey, T> _data;

		public InMemoryAppendStorage([FromKeyedServices("Synqra.AppendStorage.InMemory")] Func<T, TKey> getKey)
		{
			_getKey = getKey;
			// Guid keys order by their bytes: v7 time order, the wire's and Mongo's order, the same on every target
			_data = typeof(TKey) == typeof(Guid)
				? new SortedDictionary<TKey, T>((IComparer<TKey>)Comparer<Guid>.Create((a, b) => a.CompareBigEndian(b)))
				: new SortedDictionary<TKey, T>();
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
			var hasCursor = from is not null && !Equals(from, default(TKey));
			foreach (var item in data.SkipWhile(x => hasCursor && _data.Comparer.Compare(x.Key, from!) <= 0))
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
