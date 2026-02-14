using Contoso.Projection.InMemory;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Synqra;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddKeyedSingleton<IProjection, ContosoInMemoryProjection>("localOnlyProjection");

await builder.Build().RunAsync();
