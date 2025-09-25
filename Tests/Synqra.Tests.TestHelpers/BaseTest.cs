
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Synqra.Tests.TestHelpers;

public abstract class BaseTest : PerformanceTestUtils
{
	public IServiceCollection ServiceCollection => HostBuilder.Services;

	IConfiguration? _configuration;
	public IConfiguration Configuration => _configuration ?? HostBuilder.Configuration;

	HostApplicationBuilder? _hostBuilder;

	public IHostApplicationBuilder HostBuilder
	{
		get
		{
			if (_hostBuilder is null)
			{
				// Console.Write("AppContext.BaseDirectory = ");
				// Console.WriteLine(AppContext.BaseDirectory);
				_hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
				{
					ContentRootPath = AppContext.BaseDirectory,
					EnvironmentName = "Development",
				});
				_configuration = _hostBuilder.Configuration;
			}
			return _hostBuilder;
		}
	}

	// ------------------------------- Host Time -------------------------------

	IHost? _host;

	public IHost ApplicationHost
	{
		get
		{
			if (_host is null)
			{
				_host = (_hostBuilder ?? (HostApplicationBuilder)HostBuilder).Build();
			}
			return _host;
		}
	}

	public IServiceProvider ServiceProvider => ApplicationHost.Services;


	#region TestHelpers

	public Random RandomShared = new Random();

	#endregion
}
