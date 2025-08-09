using System.Diagnostics.CodeAnalysis;
using System.Collections;

namespace Synqra;

public interface IStoreCollection : IList, ICollection//, IQueryable, INotifyCollectionChanged
{
}

public interface IStoreCollection<T> : IStoreCollection, ICollection<T>//, IQueryable<T>, INotifyCollectionChanged
	where T : class
{
#if NET8_0_OR_GREATER
	new T this[int index]
	{
		get => ((IReadOnlyList<T>)this)[index];
		set => throw new NotSupportedException("StoreCollection is read-only, use Add() to add new items");
	}
#else
	new T this[int index] { get; set; }
#endif
}

public interface IStoreCollectionInternal : IStoreCollection
{
	ISynqraStoreContext Store { get; }

	Type Type { get; }

	void AddByEvent(object item);
#if NETSTANDARD
	bool TryGetAttached(Guid id, out object? item);
#else
	bool TryGetAttached(Guid id, [NotNullWhen(true)] out object? item);
#endif
	Guid GetId(object item);
}
