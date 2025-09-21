using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

class StoreCollection<T> : IStoreCollection<T>, IStoreCollectionInternal, IReadOnlyList<T>
	where T : class
{
	// Remember - this is always client request, not a synchronization!
	// Client requests are converted to commands and then processed to events and then aggregated here in state processor
	private readonly StoreContext _storeContext;
#if NET8_0_OR_GREATER
	private readonly JsonSerializerContext _jsonSerializerContext;
#endif

	/// <summary>
	/// Additional objects that are attached to this collection but not yet added to the list. Eg inserting new item.
	/// </summary>
	private readonly Dictionary<Guid, WeakReference<T>> _attachedObjects = new();
	private readonly ConditionalWeakTable<object, Tuple<Guid>> _attachedIds = new();
	private byte _attachedMaintain;
	private readonly List<T> _list = new List<T>();

	ISynqraStoreContext IStoreCollectionInternal.Store => _storeContext;
	public Type Type => typeof(T);

	public StoreCollection(StoreContext ctx
#if NET8_0_OR_GREATER
		, JsonSerializerContext jsonSerializerContext
#endif
		)
	{
		_storeContext = ctx;
#if NET8_0_OR_GREATER
		_jsonSerializerContext = jsonSerializerContext;
#endif
	}

#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
#else
	public T this[int index]
	{
		get => ((IReadOnlyList<T>)this)[index];
		set => throw new NotSupportedException("StoreCollection is read-only, use Add() to add new items");
	}
#endif

	public bool TryGetAttached(Guid id,
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
		[NotNullWhen(true)]
#endif
		out object? attached
		)
	{
		lock (_attachedObjects)
		{
			if (_attachedObjects.TryGetValue(id, out var weakRef) && weakRef.TryGetTarget(out var target))
			{
				attached = target;
				return true;
			}
			attached = null;
		}
		return false;
	}

	public Guid GetId(object attached)
	{
		if (attached == null)
		{
			throw new ArgumentNullException(nameof(attached), "Attached object cannot be null.");
		}
		lock (_attachedObjects) // this is a root lock
		{
			if (_attachedIds.TryGetValue(attached, out var tuple))
			{
				return tuple.Item1;
			}
		}
		return default;
	}

	#region BY INDEX

	object? IList.this[int index]
	{
		get => ((IList)_list)[index];
		set => throw new NotImplementedException();
	}

	T IReadOnlyList<T>.this[int index] => _list[index];

	#endregion

	#region Informational

	bool IList.IsFixedSize => false;

	bool IList.IsReadOnly => throw new NotImplementedException(); // this actually depends on a model, do we allow primitive automatic commands or not

	bool ICollection<T>.IsReadOnly => throw new NotImplementedException();

	bool ICollection.IsSynchronized => throw new NotImplementedException();

	object ICollection.SyncRoot => _list;

	/*
	Type IQueryable.ElementType => typeof(T);

	Expression IQueryable.Expression => throw new NotImplementedException();

	IQueryProvider IQueryable.Provider => throw new NotImplementedException();
	*/

	#endregion

	#region COUNT

	/*
	int ICollection<T>.Count => _list.Count;
	int ICollection.Count => _list.Count;
	int IReadOnlyCollection<T>.Count => _list.Count;
	*/

	public int Count => _list.Count;

	#endregion

	#region Add

	int IList.Add(object? value)
	{
		if (value is not T item)
		{
			throw new ArgumentException($"Value must be of type {typeof(T).Name}", nameof(value));
		}
		return Add(item);
	}

	void ICollection<T>.Add(T item)
	{
		Add(item);
	}

	void IList.Insert(int index, object? value)
	{
		throw new NotSupportedException();
	}

	// Client request - generate command
	private int Add(T item)
	{
		var o = ((ICollection)this).Count;
		var dataJson = JsonSerializer.Serialize(item
#if NET8_0_OR_GREATER
			, _jsonSerializerContext.Options
#endif
			);
		var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson
#if NET8_0_OR_GREATER
			, _jsonSerializerContext.Options
#endif
			);
		var targetId = GuidExtensions.CreateVersion7(); // This is a new object, so we generate a new object ID
		lock (_attachedObjects)
		{
			_attachedObjects[targetId] = new WeakReference<T>(item); // attach item to the collection, so it can be retrieved later if needed
			_attachedIds.Add(item, Tuple.Create(targetId));
			if (++_attachedMaintain == 0)
			{
				// clean up weak references
				foreach (var key in _attachedObjects.Keys.ToArray())
				{
					if (!_attachedObjects[key].TryGetTarget(out _))
					{
						_attachedObjects.Remove(key);
					}
				}
			}
		}
		_storeContext.SubmitCommandAsync(new CreateObjectCommand
		{
			TargetTypeId = _storeContext.GetTypeMetadata(typeof(T)).TypeId,
			CommandId = GuidExtensions.CreateVersion7(), // This is a new object, so we generate a new command Id
			TargetId = targetId,
			Data = data?.Count > 0 ? data : null,
			DataJson = dataJson,
			DataObject = item,
		}).GetAwaiter().GetResult();
		var n = ((ICollection)this).Count;
		return n == o ? n + 1 : n; // if it is not changed, then it will be next index, if updated, then new count is actual index
	}

	void IStoreCollectionInternal.AddByEvent(object item)
	{
		if (item is not T typedItem)
		{
			throw new ArgumentException($"Item must be of type {typeof(T).Name}", nameof(item));
		}
		_list.Add(typedItem);
	}

#endregion

	#region Remove

	void IList.Clear()
	{
		throw new NotSupportedException();
	}

	void ICollection<T>.Clear()
	{
		throw new NotSupportedException();
	}

	void IList.Remove(object? value)
	{
		throw new NotImplementedException();
	}

	bool ICollection<T>.Remove(T item)
	{
		throw new NotImplementedException();
	}

	void IList.RemoveAt(int index)
	{
		throw new NotImplementedException();
	}

	#endregion

	#region Contains

	bool IList.Contains(object? value)
	{
		throw new NotImplementedException();
	}

	bool ICollection<T>.Contains(T item)
	{
		throw new NotImplementedException();
	}

	#endregion

	#region Iterate

	void ICollection.CopyTo(Array array, int index)
	{
		throw new NotImplementedException();
	}

	void ICollection<T>.CopyTo(T[] array, int arrayIndex)
	{
		throw new NotImplementedException();
	}

	IEnumerator<T> IEnumerable<T>.GetEnumerator()
	{
		throw new NotImplementedException();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		throw new NotImplementedException();
	}

	int IList.IndexOf(object? value)
	{
		throw new NotImplementedException();
	}

	#endregion

	/*
	event NotifyCollectionChangedEventHandler? INotifyCollectionChanged.CollectionChanged
	{
		add
		{
			throw new NotImplementedException();
		}

		remove
		{
			throw new NotImplementedException();
		}
	}
	*/
}