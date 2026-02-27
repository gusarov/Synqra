using Synqra.BinarySerializer;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synqra;

internal abstract class StoreCollection : ISynqraCollection
{
	// Remember - this is always client request, not a synchronization!
	// Client requests are converted to commands and then processed to events and then aggregated here in state processor
	private protected readonly JsonSerializerOptions? _jsonSerializerOptions;

	internal IObjectStore Store { get; private init; }
	internal Guid ContainerId { get; private init; }
	internal Guid CollectionId { get; private init; }

	internal readonly ISBXSerializerFactory _serializerFactory;

	public abstract Type Type { get; }
	protected abstract IList IList { get; }
	protected abstract ICollection ICollection { get; }

#if DEBUG
	internal IList DebugList => IList;
#endif

	public StoreCollection(IObjectStore store
		, Guid containerId
		, Guid collectionId
		, ISBXSerializerFactory serializerFactory
#if NET8_0_OR_GREATER
		, JsonSerializerOptions? jsonSerializerOptions = null
#endif
		)
	{
		Store = store ?? throw new ArgumentNullException(nameof(store));
		ContainerId = containerId;
		CollectionId = collectionId;
		_serializerFactory = serializerFactory ?? throw new ArgumentNullException(nameof(serializerFactory)); ;
		_jsonSerializerOptions = jsonSerializerOptions;
	}

	public int Count => IList.Count;

	internal abstract void AddByEvent(object item);
}
