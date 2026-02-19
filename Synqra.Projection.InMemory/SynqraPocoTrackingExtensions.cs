using System.Collections.Concurrent;
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

		ConcurrentDictionary<object, string> _originalsSerialized = new();

		public TrackingSessionImplementation(StoreCollection storeCollection, IEnumerable<object> items)
		{
			_storeCollection = storeCollection;

			foreach (var item in items)
			{
				AddCore(item);
			}
		}

		public void Add(object item)
		{
			lock (_originalsSerialized)
			{
				AddCore(item);
			}
		}

		void AddCore(object item)
		{
			_storeCollection.Projection.GetId(item, null, GetMode.RequiredId); // ensure attached
			_originalsSerialized[item] = JsonSerializer.Serialize(item, _storeCollection.Type
#if NET8_0_OR_GREATER
				, ((InMemoryProjection)_storeCollection.Projection)._jsonSerializerOptions
#endif
				);
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
			// compare and submit changes
			foreach (var kvp in _originalsSerialized)
			{
				// serialzie again
				var json = JsonSerializer.Serialize(kvp.Key, _storeCollection.Type
#if NET8_0_OR_GREATER
					, ((InMemoryProjection)_storeCollection.Projection)._jsonSerializerOptions
#endif
					);
				if (json != kvp.Value)
				{
					// changed!!
					var original = JsonSerializer.Deserialize<IDictionary<string, object?>>(kvp.Value
#if NET8_0_OR_GREATER
						, ((InMemoryProjection)_storeCollection.Projection)._jsonSerializerOptions
#endif
						);
					var updated = JsonSerializer.Deserialize<IDictionary<string, object?>>(json
#if NET8_0_OR_GREATER
						, ((InMemoryProjection)_storeCollection.Projection)._jsonSerializerOptions
#endif
						);
					foreach (var item in original.Keys.Union(updated.Keys))
					{
						var oldValue = original.TryGetValue(item, out var ov) ? ov : null;
						var newValue = updated.TryGetValue(item, out var nv) ? nv : null;
						if (oldValue != newValue)
						{
							await _storeCollection.Projection.SubmitCommandAsync(new ChangeObjectPropertyCommand
							{
								CommandId = GuidExtensions.CreateVersion7(),
								ContainerId = _storeCollection.ContainerId,
								TargetTypeId = ((InMemoryProjection)_storeCollection.Projection).GetTypeMetadata(_storeCollection.Type).TypeId,
								PropertyName = item,
								OldValue = oldValue,
								NewValue = newValue,
								TargetId = _storeCollection.Projection.GetId(kvp.Key, null, GetMode.RequiredId),
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
