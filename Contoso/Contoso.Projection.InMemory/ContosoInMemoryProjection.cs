using Contoso.Model;
using Synqra;
using Synqra.AppendStorage;
using Synqra.BinarySerializer;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Contoso.Projection.InMemory;

public class LocalOnlyContosoInMemoryProjection : ContosoInMemoryProjection
{
	public LocalOnlyContosoInMemoryProjection(ISbxSerializerFactory serializerFactory, ITypeMetadataProvider typeMetadataProvider, Guid streamId) : base(
		  serializerFactory: serializerFactory
		, typeMetadataProvider: typeMetadataProvider
		, streamId: streamId
		, eventStorage: null
		, eventReplicationService: null
		, jsonSerializerOptions: null
		, jsonSerializerContext: null
		)
	{
	}
}

public class ContosoInMemoryProjection : InMemoryProjection, IContosoCommandVisitor<CommandHandlerContext>, IContosoEventVisitor<EventVisitorContext>
{
	public ContosoInMemoryProjection(
		  ISbxSerializerFactory serializerFactory
		, ITypeMetadataProvider typeMetadataProvider
		, Guid streamId
		, IAppendStorage<Event, Guid>? eventStorage = null
		, IAppendStorage<ProjectionSnapshot, Guid>? snapshotStorage = null
		, IEventReplicationService? eventReplicationService = null
		, JsonSerializerOptions? jsonSerializerOptions = null
		, JsonSerializerContext? jsonSerializerContext = null
		) : base(
			  serializerFactory
			, typeMetadataProvider
			, streamId
			, eventStorage
			, eventReplicationService
			, jsonSerializerOptions
			, jsonSerializerContext
			)
	{
	}

	public Task VisitAsync(FooContosoCommand command, CommandHandlerContext ctx)
	{
		return Task.CompletedTask;
	}

	public Task VisitAsync(FooContosoEvent ev, EventVisitorContext ctx)
	{
		return Task.CompletedTask;
	}
}
