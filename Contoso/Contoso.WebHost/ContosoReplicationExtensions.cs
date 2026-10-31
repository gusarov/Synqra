using Contoso.Model;
using Contoso.Projection.InMemory;
using MongoDB.Driver;
using Synqra;
using Synqra.AppendStorage.InMemory;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;
using Synqra.Projection.InMemory;
using Synqra.Projection.MongoDb;
using Synqra.Replication.AspNetCore;
using System.Text.Json.Serialization;

namespace Contoso.WebHost;

public static class ContosoReplicationExtensions
{
	/// <summary>
	/// The Contoso master: its log and store, the replication endpoint's dependencies, and the seeder for the streams it creates. By
	/// default the log and the projections are in memory; with <c>Contoso:Master = Mongo</c> they are the Mongo log and projection a
	/// production host runs (<c>ConnectionStrings:mongodb</c>, database <c>Contoso:Database</c>). Either serves stream snapshots.
	/// </summary>
	public static IServiceCollection AddContosoMaster(this IServiceCollection services, IConfiguration configuration)
	{
		var scene = new ContosoScene();
		services.AddSingleton(scene);
		services.AddSbxSerializer(ser =>
		{
			ser.Map(100, typeof(ContosoItem));
			ser.Map(101, typeof(FooContosoCommand));
			ser.Map(102, typeof(FooContosoEvent));
			ser.Map(103, typeof(ContosoSceneNode));
			ser.Map(104, typeof(ContosoScenePosition));
			ser.Map(105, typeof(ContosoSceneTag));
			ser.Map(106, typeof(ContosoSceneEdge));
		});
		services.AddSingleton<INetworkSerializationService, SbxNetworkSerializationService>();
		services.AddTypeMetadataProvider([typeof(ContosoItem), typeof(FooContosoCommand), typeof(FooContosoEvent), .. scene.Types]);
		services.AddSingleton<JsonSerializerContext>(ContosoJsonSerializerContext.Default);
		services.AddSingleton(ContosoJsonSerializerContext.DefaultOptions);
		if (string.Equals(configuration["Contoso:Master"], "Mongo", StringComparison.OrdinalIgnoreCase))
		{
			var connectionString = configuration.GetConnectionString("mongodb")
				?? throw new InvalidOperationException("Contoso:Master is Mongo, but ConnectionStrings:mongodb is not set.");
			services.AddSingleton<IMongoClient>(_ =>
			{
				new ContosoMongoReplicaSet(connectionString).EnsureInitiated();
				return new MongoClient(connectionString);
			});
			services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(configuration["Contoso:Database"] ?? "contoso"));
			services.AddMongoEventStorage();
			services.AddServerEventLog();
			services.AddMongoProjection(); // the store the endpoint applies peers' events to, the seed is written through, and snapshots are cut from
			services.AddSingleton<IContosoMasterStore, MongoContosoMasterStore>();
		}
		else
		{
			services.AddAppendStorageInMemory<Event, (Guid, Guid)>(x => (x.StreamId, x.SequenceId));
			services.AddServerEventLog();
			services.AddInMemorySynqraStore<ContosoInMemoryProjection>(); // the seed is written through the stream's projection
			services.AddInMemoryStreamSnapshots();
			services.AddSingleton<IProjection, ContosoLogOnlyProjection>();
			services.AddSingleton<IContosoMasterStore, InMemoryContosoMasterStore>();
		}
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
		app.MapPost("/api/streams/{stream:guid}/items", async (Guid stream, ContosoItemRequest request, IContosoMasterStore stores) =>
		{
			// A server-side write (REST, a background job): it lands in the log like any browser's write.
			await stores.RunAsync(stream, async store =>
			{
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
				return 0;
			});
			return Results.Ok();
		});
		// The scene as the master holds it, the same text a browser shows, so a test compares the two.
		app.MapGet("/api/streams/{stream:guid}/scene", async (Guid stream, IContosoMasterStore stores, ContosoScene scene)
			=> Results.Text(await stores.RunAsync(stream, store => Task.FromResult(scene.Describe(store)))));
		// A scene the server builds (as an import or a job would): a browser that opens the stream later loads it.
		app.MapPost("/api/streams/{stream:guid}/scene", async (Guid stream, ContosoSceneRequest request, IContosoMasterStore stores, ContosoScene scene) =>
		{
			await stores.RunAsync(stream, async store =>
			{
				await scene.BuildAsync(store, request.Prefix, request.Nodes);
				return 0;
			});
			return Results.Ok();
		});
		app.MapPost("/api/streams/{stream:guid}/scene/rename", async (Guid stream, ContosoRenameRequest request, IContosoMasterStore stores, ContosoScene scene) =>
		{
			await stores.RunAsync(stream, async store =>
			{
				await scene.RenameFirstAsync(store, request.Name);
				return 0;
			});
			return Results.Ok();
		});
		return app;
	}
}

public sealed record ContosoStreamCreated(Guid StreamId);

public sealed record ContosoStreamLog(int Events, int Welcomes);

public sealed record ContosoItemRequest(string ItemName);

public sealed record ContosoSceneRequest(string Prefix, int Nodes);

public sealed record ContosoRenameRequest(string Name);
