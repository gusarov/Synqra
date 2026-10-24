using Contoso.Wasm;
using Contoso.Wasm.OpfsPoc;
using Contoso.Model;
using Contoso.Projection.InMemory;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Synqra;
using Synqra.AppendStorage.BlobStorage.IndexedDb;
using Synqra.BinarySerializer;
using Synqra.Projection.InMemory;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Serialization;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// The page's stream is read before the host is built: it names the IndexedDB store the log lives in.
var seedingStream = new SeedingStream(JSHost.GlobalThis.GetPropertyAsJSObject("location")!.GetPropertyAsString("href")!);
builder.Services.AddSingleton(seedingStream);

// Event Storage
if (seedingStream.IsSelected)
{
	builder.Services.AddEventStorageBlobIndexedDb(builder.Configuration, seedingStream.StoreName); // the browser event log Quotaly runs, one store per stream
}
else
{
	builder.Services.AddAppendStorageBlobIndexedDb<Event>(x => (x.StreamId, x.SequenceId), builder.Configuration, seedingStream.StoreName);
}
builder.Services.AddClientEventLog();
builder.Services.AddSbxSerializer(ser =>
{
	ser.Map(100, typeof(ContosoItem));
	ser.Map(101, typeof(FooContosoCommand));
	ser.Map(102, typeof(FooContosoEvent));
});

// Projection
builder.Services.AddTypeMetadataProvider(
	typeof(ContosoItem),
	typeof(FooContosoCommand),
	typeof(FooContosoEvent));
builder.Services.AddSingleton<JsonSerializerContext>(ContosoJsonSerializerContext.Default);
builder.Services.AddSingleton(ContosoJsonSerializerContext.DefaultOptions);
// Projection is non-multitenant in-memory: no DI-resolvable singleton — the factory/provider produce
// a ContosoInMemoryProjection per stream at the call site (Home.razor resolves it for the Contoso
// feature stream via IProjectionProvider). The IndexedDb event store above stays a singleton.
builder.Services.AddInMemorySynqraStore<ContosoInMemoryProjection>();
builder.Services.AddScoped<OpfsPocJsInterop>();

// Replication, for a page load that names its stream (the seeding page). The seeder is registered only to seed the streams this
// browser creates: no replication path runs it, so a browser that opens a stream it did not create never seeds it.
builder.Services.AddLazyServiceResolution();
builder.Services.AddSingleton<INetworkSerializationService, SbxNetworkSerializationService>();
builder.Services.AddSingleton<EventReplicationState>();
builder.Services.AddSingleton<EventReplicationConfig>(sp => sp.GetRequiredService<SeedingStream>().CreateReplicationConfig());
builder.Services.AddSingleton<EventReplicationService>();
builder.Services.AddSingleton<IEventReplicationService>(sp => sp.GetRequiredService<EventReplicationService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventReplicationService>());
builder.Services.AddStreamSeeder<ContosoWelcomeSeeder>();

var host = builder.Build();
if (seedingStream.IsSelected)
{
	await host.Services.StartHostedServicesAsync(); // WASM has no hosted-service pump
}
await host.RunAsync();
