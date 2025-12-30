
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Synqra.BinarySerializer;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace Synqra.Tests.TestHelpers;

public abstract class BaseTest<T> : BaseTest where T : notnull
{
	public T _sut => ServiceProvider.GetRequiredService<T>();
}

public class TestUtils : PerformanceTestUtils
{
	public Random RandomShared = new Random();
	public HexDumpWriter HexDumpWriter = new HexDumpWriter();

	public string CreateTestFileName(string fileName)
	{
		return Path.Combine(CreateTestFolder(), fileName);
	}

	public string CreateTestFolder()
	{
		var synqraTestsPath = Path.Combine(Path.GetTempPath(), "SynqraTests");
		Directory.CreateDirectory(synqraTestsPath);
		// Clean up old test folders
		var synqraNewTestPath = Path.Combine(synqraTestsPath, DateTime.Now.ToString("yyyy-MM-dd_HH-00")); // pattern stands for an hour
		foreach (var item in Directory.GetDirectories(synqraTestsPath))
		{
			if (item != synqraNewTestPath)
			{
				try
				{
					Directory.Delete(item, true);
				}
				catch
				{
				}
			}
		}
		synqraNewTestPath = Path.Combine(synqraNewTestPath, Guid.NewGuid().ToString());
		Directory.CreateDirectory(synqraNewTestPath);
		return synqraNewTestPath;
	}

	public string FileReadAllText(string fileName)
	{
		// Console.WriteLine("FileReadAllText: " + fileName);
		// EmergencyLog.Default.Message("FileReadAllText: " + fileName + "\r\n" + new StackTrace());
		using var sr = new StreamReader(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite /* Main Difference */), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 64);
		return sr.ReadToEnd();
	}

	public void HexDump(ReadOnlySpan<byte> data, SBXSerializer? serializer = null)
	{
		Console.WriteLine();
		HexDumpWriter.HexDump(data, Console.Write, Console.Write);
		Console.WriteLine();

#if DEBUG
		if (serializer is not null)
		{
			Console.WriteLine("Tokenized:");
			foreach (var item in serializer.Tokens)
			{
				Console.WriteLine(item.Item3);
				HexDump(data[item.Item1..(item.Item2-1)]);
			}
			Console.WriteLine();
		}
#endif

		Console.WriteLine();
	}
}

public class BaseTest : TestUtils
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
}
