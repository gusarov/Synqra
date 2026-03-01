using System.Collections;
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
		, Guid containerId
		, Guid collectionId
		, ISBXSerializerFactory serializerFactory
		) : base(
		  store
		, containerId
		, collectionId
		, serializerFactory
		)
	{
	}

	internal abstract void AddByEvent(object item);
}

internal class InMemoryStoreCollection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T> : InMemoryStoreCollection, ISynqraCollection<T>, IReadOnlyList<T>
	where T : class
{
	private readonly List<T> _list = new List<T>();

	public override Type Type => typeof(T);
	/*
	protected override IList IList => _list;
	protected override ICollection ICollection => _list;
	*/

	public int Count => _list.Count;

	InMemoryProjection _store;

	public InMemoryStoreCollection(
		  IObjectStore store
		, Guid containerId
		, Guid collectionId
		, ISBXSerializerFactory serializerFactory
		, JsonSerializerOptions? jsonSerializerOptions = null
		)
		: base(
			  store
			, containerId
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

	T IReadOnlyList<T>.this[int index] => _list[index];

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
		var o = _list.Count;

		// var dataJson = _jsonSerializerOptions == null ? null : JsonSerializer.Serialize(item, _jsonSerializerOptions);
		// var data = _jsonSerializerOptions == null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson, _jsonSerializerOptions);

		var attachedData = Store.Attach(item, this);
		var task = Store.SubmitCommandAsync(new CreateObjectCommand
		{
			ContainerId = ContainerId,
			CollectionId = CollectionId,
			TargetTypeId = _store.TypeMetadataProvider.GetTypeMetadata(typeof(T)).TypeId,
			CommandId = GuidExtensions.CreateVersion7(), // This is a new object, so we generate a new command Id
			TargetId = attachedData.Id,
			Data = item, // data?.Count > 0 ? data : null,
						 // DataJson = dataJson,
			TargetObject = item,
		});
		if (!OperatingSystem.IsBrowser())
		{
			task.GetAwaiter().GetResult();
		}
		var n = _list.Count;
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
		_list.Add(typedItem);
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
		if (array.Length < _list.Count + arrayIndex)
		{
			throw new ArgumentException("Array is too small to copy the collection.", nameof(array));
		}
		for (int i = 0, m = Count; i < m; i++)
		{
			array.SetValue(_list[i], arrayIndex + i);
		}
	}
#endif
	void ICollection<T>.CopyTo(T[] array, int arrayIndex)
	{
		if (array.Length < _list.Count + arrayIndex)
		{
			throw new ArgumentException("Array is too small to copy the collection.", nameof(array));
		}
		for (int i = 0, m = Count; i < m; i++)
		{
			array[arrayIndex + i] = _list[i];
		}
	}

	IEnumerator<T> IEnumerable<T>.GetEnumerator()
	{
		return _list.GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return _list.GetEnumerator();
	}

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