using Contoso.Wasm.OpfsPoc;
using Contoso.Model;
using Contoso.Projection.InMemory;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Synqra;
using Synqra.BlobStorage.IndexedDb;
using Synqra.BinarySerializer;
using System.Text.Json.Serialization;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Event Storage
builder.Services.AddBlobStorageIndexedDb<Event>(x => x.EventId, builder.Configuration);
// builder.Services.AddSingleton<Func<Event, Guid>>(x => x.EventId);
builder.Services.AddSbxSerializer(ser =>
{
	ser.Map(100, typeof(ContosoItem));
	ser.Map(101, typeof(FooContosoCommand));
	ser.Map(102, typeof(FooContosoEvent));
	ser.Snapshot();
});

// Projection
builder.Services.AddTypeMetadataProvider(
	typeof(ContosoItem),
	typeof(FooContosoCommand),
	typeof(FooContosoEvent));
builder.Services.AddSingleton<JsonSerializerContext>(ContosoJsonSerializerContext.Default);
builder.Services.AddSingleton(ContosoJsonSerializerContext.DefaultOptions);
builder.Services.AddSingleton<ContosoInMemoryProjection>();
builder.Services.AddScoped<OpfsPocJsInterop>();

await builder.Build().RunAsync();
