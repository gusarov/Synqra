using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.BinarySerializer;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

public abstract class StoreCollection : ISynqraCollection
{
	public IObjectStore Store { get; }
	public Guid ContainerId { get; }
	public Guid CollectionId { get; }
	public ISBXSerializerFactory SerializerFactory { get; }

	public abstract Type Type { get; }

	/*
	protected abstract IList IList { get; }
	protected abstract ICollection ICollection { get; }

#if DEBUG
	internal IList DebugList => IList;
#endif
	*/

	public StoreCollection(
		  IObjectStore store
		, Guid containerId
		, Guid collectionId
		, ISBXSerializerFactory serializerFactory
		)
	{
		Store = store ?? throw new ArgumentNullException(nameof(store));
		ContainerId = containerId;
		CollectionId = collectionId;
		if (containerId == default)
		{
			throw new ArgumentException("", nameof(containerId));
		}
		if (collectionId == default)
		{
			throw new ArgumentException("", nameof(collectionId));
		}
		SerializerFactory = serializerFactory ?? throw new ArgumentNullException(nameof(serializerFactory)); ;
	}

	// public int Count => IList.Count;

	public abstract IEnumerator GetEnumerator();
}

internal static class SynqraCollectionInternalExtensions
{
	public static IObjectStore GetStore(this ISynqraCollection collection)
	{
		if (collection is StoreCollection internalCollection)
		{
			return internalCollection.Store;
		}
		throw new InvalidOperationException("Collection does not implement StoreCollection");
	}

	public static Type GetElementType(this ISynqraCollection collection)
	{
		if (collection is StoreCollection internalCollection)
		{
			return internalCollection.Type;
		}
		throw new InvalidOperationException("Collection does not implement StoreCollection");
	}
}
