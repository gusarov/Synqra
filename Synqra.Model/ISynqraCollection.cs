using System.Diagnostics.CodeAnalysis;
using System.Collections;

namespace Synqra;

public interface ISynqraCollection : IEnumerable //: IList //, ICollection//, IQueryable, INotifyCollectionChanged
{
}

public interface ISynqraCollection<T> : ISynqraCollection, IEnumerable<T>, ICollection<T>//, IQueryable<T>, INotifyCollectionChanged
	where T : class
{
	/*
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
	T this[int index]
	{
		get => ((IReadOnlyList<T>)this)[index];
		set => throw new NotSupportedException("StoreCollection is read-only, use Add() to add new items");
	}
#else
	new T this[int index] { get; set; }
#endif
	*/
}
