using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Synqra.BinarySerializer;
using Synqra.Projection;

namespace Synqra.Projection.InMemory;

internal abstract class InMemoryStoreCollection : StoreCollection, ISynqraCollection
{
	public InMemoryStoreCollection(
		  IObjectStore store
		, Guid streamId
		, Guid collectionId
		, ISbxSerializerFactory serializerFactory
		) : base(
		  store
		, streamId
		, collectionId
		, serializerFactory
		)
	{
	}

	internal abstract void AddByEvent(object item);
	internal abstract void RemoveByEvent(object item);
}

internal class InMemoryStoreCollection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T> : InMemoryStoreCollection, ISynqraCollection<T>, IReadOnlyList<T>
	where T : class
{
	// A store is a process-wide singleton per stream, so a hosted service writing on a timer beside a
	// request thread reading is the ordinary case, not an exotic one. A bare List<T> made that an
	// InvalidOperationException ("Collection was modified; enumeration operation may not execute")
	// waiting to happen. ConcurrentAppendList keeps insertion order — which is load-bearing, callers
	// do reach for "the last one added" — while letting readers enumerate without copying or blocking.
	private readonly ConcurrentAppendList<T> _items = new ConcurrentAppendList<T>();


	public override Type Type => typeof(T);
	/*
	protected override IList IList => _list;
	protected override ICollection ICollection => _list;
	*/

	public int Count => _items.Count;

	InMemoryProjection _store;

	public InMemoryStoreCollection(
		  IObjectStore store
		, Guid streamId
		, Guid collectionId
		, ISbxSerializerFactory serializerFactory
		, JsonSerializerOptions? jsonSerializerOptions = null
		)
		: base(
			  store
			, streamId
			, collectionId
			, serializerFactory
			)
	{
		_store = (InMemoryProjection)store;
	}

	#region BY INDEX

#if ILIST
	object? IList.this[int index]
	{
		get => IList[index];
		set => throw new NotImplementedException();
	}
#endif

	// Positional access is O(n) now that the backing store is append-ordered rather than an array.
	// Kept working rather than removed because IReadOnlyList<T> is part of the public shape; nothing
	// in the solution indexes a store collection positionally.
	T IReadOnlyList<T>.this[int index] => _items.ElementAt(index);

	/*
	T ISynqraCollection<T>.this[int index]
	{
		get => _list[index];
		set => throw new NotImplementedException();
	}
	*/

	#endregion

	#region Informational

#if ILIST
	bool IList.IsFixedSize => false;

	bool IList.IsReadOnly => throw new NotImplementedException(); // this actually depends on a model, do we allow primitive automatic commands or not
#endif

#if ICOLLECTION
	bool ICollection.IsSynchronized => throw new NotImplementedException();

	object ICollection.SyncRoot => ICollection;
#endif

	bool ICollection<T>.IsReadOnly => throw new NotImplementedException();

	#endregion

	#region Add

	void ICollection<T>.Add(T item)
	{
		Add(item);
	}

#if ILIST
	int IList.Add(object? value)
	{
		if (value is not T item)
		{
			throw new ArgumentException($"Value must be of type {typeof(T).Name}", nameof(value));
		}
		return Add(item);
	}

	void IList.Insert(int index, object? value)
	{
		throw new NotSupportedException();
	}
#endif

	// Client request - generate command
	private int Add(T item)
	{
		var o = _items.Count;

		// var dataJson = _jsonSerializerOptions == null ? null : JsonSerializer.Serialize(item, _jsonSerializerOptions);
		// var data = _jsonSerializerOptions == null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson, _jsonSerializerOptions);

		var attachedData = Store.Attach(item, this);
		var typeId = _store.TypeMetadataProvider.GetTypeMetadata(typeof(T)).TypeId;
		// Phase 2 (ECS): GetCollection<T>().Add always creates an ENTITY — a self-owned root component
		// (ComponentId == TargetId == entity id, _id == _eid == entityId). No object-vs-component branch.
		var task = Store.SubmitCommandAsync(new AddComponentCommand
		{
			StreamId = StreamId,
			CollectionId = CollectionId,
			TargetTypeId = typeId,
			TargetId = attachedData.Id,
			TargetObject = item,
			ComponentTypeId = typeId,
			ComponentId = attachedData.Id,
			Data = item,
		});
		if (OperatingSystem.IsBrowser())
		{
			AsyncInvoker.InvokeAsync(task);
		}
		else
		{
			task.GetAwaiter().GetResult();
		}
		var n = _items.Count;
		return n == o ? n + 1 : n; // if it is not changed, then it will be next index, if updated, then new count is actual index
	}

	internal override void AddByEvent(object item)
	{
		if (item is not T typedItem)
		{
			throw new ArgumentException($"Item must be of type {typeof(T).Name}", nameof(item));
		}
		// if (item is IIdentifiable<Guid> g)
		{
			// Store.GetAttachedData(item, g.Id, null, GetMode.GetOrCreate);
		}
		// Store.GetId(item, this, GetMode.GetOrCreate); // Ensure it is attached
		_items.Add(typedItem);
	}

	internal override void RemoveByEvent(object item)
	{
		if (item is not T typedItem)
		{
			throw new ArgumentException($"Item must be of type {typeof(T).Name}", nameof(item));
		}
		_items.Remove(typedItem);
	}

	#endregion

	#region Remove

#if ILIST
	void IList.Clear()
	{
		throw new NotSupportedException();
	}

	void IList.Remove(object? value)
	{
		throw new NotImplementedException();
	}

	void IList.RemoveAt(int index)
	{
		throw new NotImplementedException();
	}
#endif

	void ICollection<T>.Clear()
	{
		throw new NotSupportedException();
	}

	bool ICollection<T>.Remove(T item)
	{
		throw new NotImplementedException();
	}

	#endregion

	#region Contains

#if ILIST
	bool IList.Contains(object? value)
	{
		throw new NotImplementedException();
	}
#endif

	bool ICollection<T>.Contains(T item)
	{
		throw new NotImplementedException();
	}

	#endregion

	#region Iterate

#if ICOLLECTION
	void ICollection.CopyTo(Array array, int arrayIndex)
	{
		foreach (var item in _items)
		{
			if (arrayIndex >= array.Length)
			{
				throw new ArgumentException("Array is too small to copy the collection.", nameof(array));
			}
			array.SetValue(item, arrayIndex++);
		}
	}
#endif
	void ICollection<T>.CopyTo(T[] array, int arrayIndex)
	{
		_items.CopyTo(array, arrayIndex);
	}

	// Enumerates the live collection: ConcurrentQueue's enumerator walks segments in place, so this
	// neither copies the store nor blocks a concurrent writer.
	IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

	public override IEnumerator GetEnumerator()
	{
		throw new NotImplementedException();
	}

#if ILIST
	int IList.IndexOf(object? value)
	{
		throw new NotImplementedException();
	}
#endif

	#endregion
}