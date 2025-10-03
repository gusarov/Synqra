using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

abstract class StoreCollection
{
	// Remember - this is always client request, not a synchronization!
	// Client requests are converted to commands and then processed to events and then aggregated here in state processor
	private protected readonly JsonSerializerOptions _jsonSerializerOptions;

	internal StoreContext Store { get; private init; }
	internal Guid ContainerId { get; private init; }
	internal Guid CollectionId { get; private init; }

	public abstract Type Type { get; }
	protected abstract IList IList { get; }
	protected abstract ICollection ICollection { get; }

#if DEBUG
	public IList List => IList;
#endif

	public StoreCollection(StoreContext storeContext, JsonSerializerOptions jsonSerializerOptions, Guid containerId, Guid collectionId)
	{
		Store = storeContext ?? throw new ArgumentNullException(nameof(storeContext));
		_jsonSerializerOptions = jsonSerializerOptions ?? throw new ArgumentNullException(nameof(jsonSerializerOptions));
		ContainerId = containerId;
		CollectionId = collectionId;
	}

	#region COUNT

	public int Count => IList.Count;

	#endregion

	#region Add

	internal abstract void AddByEvent(object item);

	#endregion
}

class StoreCollection<T> : StoreCollection, ISynqraCollection<T>, IReadOnlyList<T>
	where T : class
{
	private readonly List<T> _list = new List<T>();

	public override Type Type => typeof(T);
	protected override IList IList => _list;
	protected override ICollection ICollection => _list;

	public StoreCollection(StoreContext storeContext, JsonSerializerOptions jsonSerializerOptions, Guid containerId, Guid collectionId)
		: base(storeContext, jsonSerializerOptions, containerId, collectionId)
	{
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

	T ISynqraCollection<T>.this[int index]
	{
		get => _list[index];
		set => throw new NotImplementedException();
	}

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
		var dataJson = JsonSerializer.Serialize(item, _jsonSerializerOptions);
		var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson, _jsonSerializerOptions);

		var attachedData = Store.Attach(item, this);
		Store.SubmitCommandAsync(new CreateObjectCommand
		{
			ContainerId = ContainerId,
			CollectionId = CollectionId,
			TargetTypeId = Store.GetTypeMetadata(typeof(T)).TypeId,
			CommandId = GuidExtensions.CreateVersion7(), // This is a new object, so we generate a new command Id
			TargetId = attachedData.Id,
			Data = data?.Count > 0 ? data : null,
			DataJson = dataJson,
			Target = item,
		}).GetAwaiter().GetResult();
		var n = _list.Count;
		return n == o ? n + 1 : n; // if it is not changed, then it will be next index, if updated, then new count is actual index
	}

	internal override void AddByEvent(object item)
	{
		if (item is not T typedItem)
		{
			throw new ArgumentException($"Item must be of type {typeof(T).Name}", nameof(item));
		}
		if (item is IIdentifiable<Guid> g)
		{
			Store.GetAttachedData(item, g.Id, null, GetMode.GetOrCreate);
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

#if ILIST
	int IList.IndexOf(object? value)
	{
		throw new NotImplementedException();
	}
#endif

	#endregion
}

internal static class SynqraCollectionInternalExtensions
{
	public static StoreContext GetStore(this ISynqraCollection collection)
	{
		if (collection is StoreCollection internalCollection)
		{
			return internalCollection.Store;
		}
		throw new InvalidOperationException("Collection does not implement StoreCollection");
	}

	public static Type GetType(this ISynqraCollection collection)
	{
		if (collection is StoreCollection internalCollection)
		{
			return internalCollection.Type;
		}
		throw new InvalidOperationException("Collection does not implement StoreCollection");
	}
}
