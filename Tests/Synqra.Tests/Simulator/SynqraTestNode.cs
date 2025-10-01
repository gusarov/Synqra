#if NETFRAMEWORK
#else
using Microsoft.AspNetCore.Builder;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synqra.Storage;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.Helpers;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra.Tests.Simulator;

internal class SynqraTestNode
{
#if NETFRAMEWORK
	public IHost Host { get; private set; }
#else
	public Microsoft.AspNetCore.Builder.WebApplication Host { get; private set; }
#endif
	ISynqraStoreContext __storeContext;

	public ISynqraStoreContext StoreContext { get =>
#if NETFRAMEWORK
			throw new NotImplementedException();
#else
			__storeContext ??= Host.Services.GetRequiredService<ISynqraStoreContext>();
#endif
	}

	public ushort Port { get; set; }

	public SynqraTestNode(Action<IHostApplicationBuilder>? configureHost = null, bool masterHost = false)
	{
		#region Folder

		var utils = new TestUtils();
		var synqraTestsCurrentPath = utils.CreateTestFolder();

		#endregion

#if NETFRAMEWORK
		throw new NotImplementedException();

		var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
		{
			ContentRootPath	= synqraTestsCurrentPath,
			EnvironmentName = Environments.Development,
		});
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["JsonLinesStorage:FileName"] = Path.Combine(synqraTestsCurrentPath, "[TypeName].jsonl"),
		});
		builder.AddSynqraStoreContext();
		builder.AddJsonLinesStorage<Event, Guid>();
		builder.Services.AddSingleton<JsonSerializerContext>(TestJsonSerializerContext.Default);
		builder.Services.AddSingleton(TestJsonSerializerContext.Default.Options);
		// builder.Services.AddSignalR();
		builder.Services.AddTransient(typeof(Lazy<>), typeof(Lazier<>));
		if (masterHost)
		{
		}
		else
		{
			builder.Services.AddHostedService<EventReplicationService>(x => x.GetRequiredService<EventReplicationService>());
			builder.Services.AddSingleton<EventReplicationService>(); // x => x.GetServices<IHostedService>().OfType<EventReplicationService>().Single());
			// builder.Services.AddSingleton<EventReplicationService>(x => x.GetServices<IHostedService>().OfType<EventReplicationService>().Single());
			builder.Services.AddSingleton<EventReplicationState>();
			// services.Configure<EventReplicationConfig>(hostContext.Configuration.GetSection(nameof(EventReplicationConfig)));
			var port = Port = GetNextAvailablePort();
			builder.Services.Configure<EventReplicationConfig>(x =>
			{
				x.Port = port;
			});
		}
		Host = builder.Build();
#else
		var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
		{
			Args = Array.Empty<string>(),
			EnvironmentName = Environments.Development,
			ContentRootPath = synqraTestsCurrentPath,
		});
		var port = Port = GetNextAvailablePort();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["JsonLinesStorage:FileName"] = Path.Combine(synqraTestsCurrentPath, "[TypeName].jsonl"),
			["URLS"] = "http://*:" + port,
		});

		builder.AddSynqraStoreContext();
		builder.AddJsonLinesStorage<Event, Guid>();
		builder.Services.AddSingleton<JsonSerializerContext>(TestJsonSerializerContext.Default);
		builder.Services.AddSingleton(TestJsonSerializerContext.Default.Options);

		builder.Services.AddSignalR();
		builder.Services.AddTransient(typeof(Lazy<>), typeof(Lazier<>));

		if (masterHost)
		{
		}
		else
		{
			builder.Services.AddHostedService<EventReplicationService>(x => x.GetRequiredService<EventReplicationService>());

			builder.Services.AddSingleton<EventReplicationService>(); // x => x.GetServices<IHostedService>().OfType<EventReplicationService>().Single());
			// builder.Services.AddSingleton<EventReplicationService>(x => x.GetServices<IHostedService>().OfType<EventReplicationService>().Single());

			builder.Services.AddSingleton<EventReplicationState>();
			// builder.Services.Configure<EventReplicationConfig>(builder.Configuration.GetSection(nameof(EventReplicationConfig)));
			builder.Services.Configure<EventReplicationConfig>(x =>
			{
				x.Port = port;
			});
		}

		configureHost?.Invoke(builder);
		var app = builder.Build();
		Host = app;

		if (masterHost)
		{
			app.MapHub<SynqraSignalerHub>("/api/synqra");
		}

		_ = Host.StartAsync();
#endif
	}

	private static ushort GetNextAvailablePort()
	{
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		var port = checked((ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
		listener.Stop();
		return port;
	}

	private class Lazier<
#if !NETFRAMEWORK
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
#endif
		T> : Lazy<T>
		where T : class
	{
		public Lazier(IServiceProvider serviceProvider)
			: base(() => serviceProvider.GetRequiredService<T>())
		{
		}
	}
}
