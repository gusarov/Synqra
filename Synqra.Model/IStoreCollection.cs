using System.Diagnostics.CodeAnalysis;
using System.Collections;

namespace Synqra;

public interface IStoreCollection : IList, ICollection//, IQueryable, INotifyCollectionChanged
{
}

public interface IStoreCollection<T> : IStoreCollection, ICollection<T>//, IQueryable<T>, INotifyCollectionChanged
	where T : class
{
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
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
	bool TryGetAttached(Guid id,
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
		[NotNullWhen(true)]
#endif
		 out object? item
		);
	Guid GetId(object item);
}
