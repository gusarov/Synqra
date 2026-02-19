using Contoso.Model;
using Contoso.Projection.InMemory;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Synqra;
using Synqra.AppendStorage.IndexedDb;
using Synqra.BinarySerializer;
using System.Text.Json.Serialization;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Event Storage
builder.Services.AddIndexedDbAppendStorage<Event, Guid>();
builder.Services.AddSingleton<Func<Event, Guid>>(x => x.EventId);
builder.Services.AddSingleton<ISBXSerializerFactory>(new SBXSerializerFactory(() =>
{
	var ser = new SBXSerializer();
	ser.Map(100, typeof(ContosoItem));
	ser.Map(101, typeof(FooContosoCommand));
	ser.Map(102, typeof(FooContosoEvent));
	ser.Snapshot();
	return ser;
}));

// Projection
builder.Services.AddSingleton<JsonSerializerContext>(ContosoJsonSerializerContext.Default);
builder.Services.AddSingleton(ContosoJsonSerializerContext.DefaultOptions);
builder.Services.AddSingleton<ContosoInMemoryProjection>();

await builder.Build().RunAsync();
