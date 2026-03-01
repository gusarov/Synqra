using Microsoft.EntityFrameworkCore;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Synqra.Projection.Sqlite;

internal class SqliteStoreCollection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T> : StoreCollection, ISynqraCollection<T>, IReadOnlyList<T>
	where T : class
{
	private readonly SqliteDatabaseContext _databaseContext;
	private readonly DbSet<T> _dbSet;

	public SqliteStoreCollection(
		  SqliteDatabaseContext databaseContext
		, DbSet<T> dbSet
		, IObjectStore store
		, Guid containerId
		, Guid collectionId
		, ISBXSerializerFactory serializerFactory
		) : base(
		  store ?? throw new ArgumentNullException(nameof(store))
		, containerId
		, collectionId
		, serializerFactory
		)
	{
		_databaseContext = databaseContext ?? throw new ArgumentNullException(nameof(databaseContext));
		_dbSet = dbSet ?? throw new ArgumentNullException(nameof(dbSet));
	}

	T IReadOnlyList<T>.this[int index] => throw new NotImplementedException();

	public override Type Type => throw new NotImplementedException();

	// protected override IList IList => throw new NotImplementedException();

	// protected override ICollection ICollection => throw new NotImplementedException();

	int ICollection<T>.Count => throw new NotImplementedException();

	int IReadOnlyCollection<T>.Count => throw new NotImplementedException();

	bool ICollection<T>.IsReadOnly => throw new NotImplementedException();
	public override IEnumerator GetEnumerator()
	{
		throw new NotImplementedException();
	}

	void ICollection<T>.Add(T item)
	{
		_dbSet.Add(item);
		_databaseContext.SaveChanges();
	}

	void ICollection<T>.Clear()
	{
		throw new NotImplementedException();
	}

	bool ICollection<T>.Contains(T item)
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

	bool ICollection<T>.Remove(T item)
	{
		throw new NotImplementedException();
	}
}
