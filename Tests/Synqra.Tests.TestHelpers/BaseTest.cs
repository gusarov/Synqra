
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Synqra.BinarySerializer;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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

	/* v1
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
		// Directory.CreateDirectory(synqraNewTestPath);
		return synqraNewTestPath;
	}
	*/

	public string CreateTestFolder()
	{
		var synqraTestsPath = Path.Combine(Path.GetTempPath(), "SynqraTests");
		Directory.CreateDirectory(synqraTestsPath);

		// Clean up old test folders
		var now = DateTime.UtcNow;
		foreach (var item in Directory.GetDirectories(synqraTestsPath))
		{
			var dir = Path.GetFileName(item);
			if (!Guid.TryParse(dir, out var id) || (now - id.GetTimestamp()).TotalHours >= 1)
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
		var synqraNewTestPath = Path.Combine(synqraTestsPath, GuidExtensions.CreateVersion7().ToString());
		return synqraNewTestPath;
	}

	public string FileReadAllText(string fileName)
	{
		// Console.WriteLine("FileReadAllText: " + fileName);
		// EmergencyLog.Default.Message("FileReadAllText: " + fileName + "\r\n" + new StackTrace());
		using var sr = new StreamReader(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite /* Main Difference */), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 64);
		return sr.ReadToEnd();
	}

	public List<string> FileReadAllLines(string fileName)
	{
		var lines = new List<string>();

		using var sr = new StreamReader(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite /* Main Difference */), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 64);
		string? line;
		while ((line = sr.ReadLine()) != null)
		{
			lines.Add(line);
		}

		return lines;
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
					EnvironmentName = "Test",
					// EnvironmentName = "Development",
				});
				_hostBuilder.Services.AddLazier();
				_origServiceCount = _hostBuilder.Services.Count;
				Register(_hostBuilder);
				_configuration = _hostBuilder.Configuration;
			}
			return _hostBuilder;
		}
	}
	protected int _origServiceCount;


	protected virtual void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
	}

	public void Restart()
	{
		_host?.Dispose();
		_host = null;
		_hostBuilder = null;
		_configuration = null;
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

public static class LazierReg
{
	public static void AddLazier(this IServiceCollection services)
	{
		services.TryAddTransient(typeof(Lazy<>), typeof(Lazier<>));
	}
}

public class Lazier<
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
