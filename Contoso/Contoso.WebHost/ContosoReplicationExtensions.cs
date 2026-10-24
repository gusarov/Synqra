using Contoso.Model;
using Contoso.Projection.InMemory;
using Synqra;
using Synqra.AppendStorage.InMemory;
using Synqra.BinarySerializer;
using Synqra.Projection.InMemory;
using Synqra.Replication.AspNetCore;

namespace Contoso.WebHost;

public static class ContosoReplicationExtensions
{
	/// <summary>The Contoso master: an in-memory server log, the replication endpoint's dependencies, and the seeder for the streams it creates.</summary>
	public static IServiceCollection AddContosoMaster(this IServiceCollection services)
	{
		services.AddAppendStorageInMemory<Event, (Guid, Guid)>(x => (x.StreamId, x.SequenceId));
		services.AddServerEventLog();
		services.AddSbxSerializer(ser =>
		{
			ser.Map(100, typeof(ContosoItem));
			ser.Map(101, typeof(FooContosoCommand));
			ser.Map(102, typeof(FooContosoEvent));
		});
		services.AddSingleton<INetworkSerializationService, SbxNetworkSerializationService>();
		services.AddTypeMetadataProvider(typeof(ContosoItem), typeof(FooContosoCommand), typeof(FooContosoEvent));
		services.AddInMemorySynqraStore<ContosoInMemoryProjection>(); // the seed is written through the stream's projection
		services.AddSingleton<IProjection, ContosoLogOnlyProjection>();
		services.AddSingleton<ContosoServerStreams>();
		services.AddSingleton<IContosoServerStreams>(sp => sp.GetRequiredService<ContosoServerStreams>());
		services.AddStreamSeeder<ContosoWelcomeSeeder>();
		return services;
	}

	/// <summary>
	/// The replication endpoint, scoped per connection to the stream it names (<c>?stream=</c>), as Quotaly's auth scopes it to the
	/// signed-in user's; the streams API a visitor gets a new stream from; and the read-only view of the log the browser tests check.
	/// </summary>
	public static WebApplication MapContosoMaster(this WebApplication app)
	{
		app.Use(next => async ctx =>
		{
			if (ctx.Request.Path.StartsWithSegments("/api/synqra/ws") && Guid.TryParse(ctx.Request.Query["stream"], out var stream) && stream != default)
			{
				using (SynqraStreamContext.Enter(stream))
				{
					await next(ctx);
				}
			}
			else
			{
				await next(ctx);
			}
		});
		app.MapSynqraReplicationEndpoint();
		app.MapPost("/api/streams", (ContosoServerStreams streams) => Results.Ok(new ContosoStreamCreated(streams.CreateStream())));
		app.MapGet("/api/streams/{stream:guid}/log", async (Guid stream, IEventLog log, CancellationToken cancellationToken) =>
		{
			var events = 0;
			var welcomes = 0;
			await foreach (var ev in log.ReadStreamAsync(stream, default, cancellationToken))
			{
				events++;
				if (ev is ComponentAddedEvent { Data: ContosoItem { ItemName: ContosoWelcomeSeeder.ItemName } })
				{
					welcomes++;
				}
			}
			return Results.Ok(new ContosoStreamLog(events, welcomes));
		});
		app.MapPost("/api/streams/{stream:guid}/items", async (Guid stream, ContosoItemRequest request, IProjectionProvider provider) =>
		{
			// A server-side write (REST, a background job): it lands in the log like any browser's write.
			var store = (IObjectStore)await provider.GetAsync(stream);
			var item = store.TypeMetadataProvider.GetTypeMetadata(typeof(ContosoItem));
			var id = store.IdProvider.CreateComponentId();
			await store.SubmitCommandAsync(new AddComponentCommand
			{
				TargetTypeId = item.TypeId,
				CollectionId = item.CollectionId,
				TargetId = id,
				ComponentTypeId = item.TypeId,
				ComponentId = id,
				Data = new ContosoItem { ItemName = request.ItemName },
			});
			return Results.Ok();
		});
		return app;
	}
}

public sealed record ContosoStreamCreated(Guid StreamId);

public sealed record ContosoStreamLog(int Events, int Welcomes);

public sealed record ContosoItemRequest(string ItemName);
