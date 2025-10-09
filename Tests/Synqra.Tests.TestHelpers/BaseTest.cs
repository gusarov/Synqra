
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using System.Diagnostics;
using System.Text;

namespace Synqra.Tests.TestHelpers;

public abstract class BaseTest<T> : BaseTest where T : notnull
{
	public T _sut => ServiceProvider.GetRequiredService<T>();
}

public class TestUtils : PerformanceTestUtils
{
	public Random RandomShared = new Random();

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

	public void HexDump(Span<byte> span)
	{
		if (span.Length <= 20)
		{
			for (int i = 0, m = span.Length; i < m; i++)
			{
				Console.Write(span[i].ToString("X2"));
				if ((i + 1) % 4 == 0)
				{
					Console.Write("  ");
				}
				else
				{
					Console.Write(" ");
				}
			}
			Console.WriteLine();
			for (int i = 0, m = span.Length; i < m; i++)
			{
				var c = (char)span[i];
				if (c == 0)
				{
					c = '.';
				}
				else if (c < 32/* || c > 126*/)
				{
					c = '?';
				}
				Console.Write("{0,2}", c);
				if ((i + 1) % 4 == 0)
				{
					Console.Write("  ");
				}
				else
				{
					Console.Write(" ");
				}
			}
			Console.WriteLine();
			Console.WriteLine();
		}
		else
		{

			int pos = 0;
			while (span.Length - pos >= 16)
			{
				for (int i = 0; i < 16; i++)
				{
					Console.Write(span[pos + i].ToString("X2"));
					if ((i + 1) % 4 == 0)
					{
						Console.Write("  ");
					}
					else
					{
						Console.Write(" ");
					}
				}
				for (int i = 0; i < 16; i++)
				{
					var c = (char)span[pos + i];
					if (c == 0)
					{
						c = '.';
					}
					else if (c < 32/* || c > 126*/)
					{
						c = '?';
					}
					Console.Write(c);
					if ((i + 1) % 4 == 0)
					{
						//Console.Write(" ");
					}
				}
				pos += 16;
				Console.WriteLine();
			}
			if (span.Length - pos > 0)
			{
				var rem = span.Length - pos;
				for (int i = 0; i < rem; i++)
				{
					Console.Write(span[pos + i].ToString("X2"));
					if ((i + 1) % 4 == 0)
					{
						Console.Write("  ");
					}
					else
					{
						Console.Write(" ");
					}
				}
				for (int i = rem; i < 16; i++)
				{
					Console.Write("  ");
					if ((i + 1) % 4 == 0)
					{
						Console.Write(" ");
					}
				}
				for (int i = 0; i < rem; i++)
				{
					var c = (char)span[pos + i];
					if (c == 0)
					{
						c = '.';
					}
					else if (c < 32/* || c > 126*/)
					{
						c = '?';
					}
					Console.Write(c);
					if ((i + 1) % 4 == 0)
					{
						Console.Write(" ");
					}
				}
				pos += rem;
			}
		}
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
