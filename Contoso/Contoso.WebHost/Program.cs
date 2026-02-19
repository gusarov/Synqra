using Contoso.Wasm.Pages;
using Contoso.WebHost.Components;
using System.Collections;
using System.Runtime.InteropServices;

var system = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine).Cast<DictionaryEntry>().Select(x => new KeyValuePair<string, string?>((string)x.Key, (string?)x.Value)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
var user = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User).Cast<DictionaryEntry>().Select(x => new KeyValuePair<string, string?>((string)x.Key, (string?)x.Value)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase); ;

foreach (var item in user)
{
	system[item.Key] = item.Value;
}

// Console.WriteLine("Non-system Env:");
foreach (var item in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Select(x => new KeyValuePair<string, string?>((string)x.Key, (string?)x.Value)).OrderBy(x => x.Key))
{
	var key = item.Key.ToUpperInvariant();
	ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(system, key, out var exists);
	if (exists && (entry?.Equals((string?)item.Value, StringComparison.OrdinalIgnoreCase) == true))
	{
		Environment.SetEnvironmentVariable(key, null);
		Console.WriteLine($"-| {item.Key}: {item.Value}");
	}
	else if (exists && (entry?.Equals((string?)item.Value, StringComparison.OrdinalIgnoreCase) != true))
	{
		Console.WriteLine($"~| {item.Key}: {item.Value} (system was {entry})");
	}
	else
	{
		Console.WriteLine($"+| {item.Key} = {item.Value}");
	}
}
Console.WriteLine("---------------------");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
	Args = args,
	WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

// Add services to the container.
builder.Services
	.AddRazorComponents()
	.AddInteractiveWebAssemblyComponents()
	;

var app = builder.Build();

Console.WriteLine("---====---");
foreach (var item in app.Configuration.AsEnumerable())
{
	Console.WriteLine(item.Key + " = " + item.Value);
}
Console.WriteLine("---====---");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.UseWebAssemblyDebugging();
}
else
{
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
	// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
	app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
	.AddInteractiveWebAssemblyRenderMode()
	.AddAdditionalAssemblies(typeof(Contoso.Wasm._Imports).Assembly);

app.Run();
