using Synqra;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Contoso.Model;

public interface IContosoObjectStore : IObjectStore
{
	ISynqraCollection<ContosoItem> Root { get; }
}

public class ContosoObjectStore : InMemoryProjection, IContosoObjectStore
{
	public ContosoObjectStore(
		ISbxSerializerFactory serializerFactory
		, ITypeMetadataProvider typeMetadataProvider
		, IAppendStorage<Event, Guid>? eventStorage = null
		, IEventReplicationService? eventReplicationService = null
		, JsonSerializerOptions? jsonSerializerOptions = null
		, JsonSerializerContext? jsonSerializerContext = null
		) : base(
			serializerFactory
			, typeMetadataProvider
			, eventStorage
			, eventReplicationService
			, jsonSerializerOptions
			, jsonSerializerContext
			)
	{
	}

	public ISynqraCollection<ContosoItem> Root
	{
		get
		{
			return GetCollection<ContosoItem>();
		}
	}
}
