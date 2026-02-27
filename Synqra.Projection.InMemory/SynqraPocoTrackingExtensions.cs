using Synqra.BinarySerializer;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Synqra.Projection.InMemory;

public static class SynqraPocoTrackingExtensions
{
	class ExtendingEntry
	{
		public string? TrackingSinceJsonSnapshot { get; set; }
	}

	public interface ITrackingSession : IDisposable, IAsyncDisposable
	{
		void Add(object item);
	}

	private sealed class TrackingSessionImplementation : ITrackingSession
	{
		private readonly StoreCollection _storeCollection;

		ConcurrentDictionary<object, byte[]> _originalsSerialized = new();
		ConcurrentDictionary<Type, bool> _typeIds = new();

		private readonly ISBXSerializer _serializer;

		public TrackingSessionImplementation(StoreCollection storeCollection, IEnumerable<object> items)
		{
			var serializerFactory = storeCollection._serializerFactory;
			_storeCollection = storeCollection;

			_serializer = serializerFactory.CreateSerializer();
			// _serializer.Snapshot();

			foreach (var item in items)
			{
				AddCore(item);
			}
		}

		int _nextTypeId;

		public void Add(object item)
		{
			lock (_originalsSerialized)
			{
				AddCore(item);
			}
		}

		void AddCore(object item)
		{
			var id = _storeCollection.Store.GetId(item, null, GetMode.RequiredId); // ensure attached

			/*
			if (_typeIds.TryAdd(item.GetType(), false))
			{
				_serializer.Map(++_nextTypeId, item.GetType());
			}
			*/

			_serializer.Reset();
			Span<byte> buffer = stackalloc byte[10240];
			var pos = 0;
			_serializer.Serialize(buffer, item, ref pos);
			_originalsSerialized[item] = buffer[..pos].ToArray();
		}

		public void Dispose()
		{
			var disposeAsync = DisposeAsync();
			if (!OperatingSystem.IsBrowser())
			{
				disposeAsync.GetAwaiter().GetResult();
			}
		}

		public async ValueTask DisposeAsync()
		{
			var buffer = new byte[1024];

			// compare and submit changes
			foreach (var kvp in _originalsSerialized)
			{
				// serialzie again
				_serializer.Reset();
				var pos = 0;
				_serializer.Serialize(buffer, kvp.Key, ref pos);
				if (!buffer[..pos].SequenceEqual(kvp.Value))
				{
					// changed!!
					_serializer.Reset();
					pos = 0;
					var original = _serializer.Deserialize<object>(kvp.Value, ref pos);
					foreach (var pi in kvp.Key.GetType().GetProperties())
					{
						var oldValue = pi.GetValue(original);
						var newValue = pi.GetValue(kvp.Key);
						if (!Equals(oldValue, newValue))
						{
							await _storeCollection.Store.SubmitCommandAsync(new ChangeObjectPropertyCommand
							{
								CommandId = GuidExtensions.CreateVersion7(),
								ContainerId = _storeCollection.ContainerId,
								TargetTypeId = ((InMemoryProjection)_storeCollection.Store).GetTypeMetadata(_storeCollection.Type).TypeId,
								PropertyName = pi.Name,
								OldValue = oldValue,
								NewValue = newValue,
								TargetId = _storeCollection.Store.GetId(kvp.Key, null, GetMode.RequiredId),
							});
						}
					}
				}
			}
		}
	}

	// private static readonly ConditionalWeakTable<object, ExtendingEntry> _attachedProperties = new();

	public static ITrackingSession PocoTracker(this ISynqraCollection collection, params IEnumerable<object> items)
	{
		/*
		var attached = _attachedProperties.GetOrCreateValue(collection);
		if (!ReferenceEquals(null, attached.TrackingSinceJsonSnapshot))
		{
			throw new Exception("Previous tracker is not closed!");
		}
		*/
		// ((IStoreCollectionInternal)collection).Store.
		return new TrackingSessionImplementation((StoreCollection)collection, items);
		// CollectionsMarshal.GetValueRefOrAddDefault(((IStoreCollectionInternal)collection)._attachedObjects, q.GetId(), out var exists);
	}
}
