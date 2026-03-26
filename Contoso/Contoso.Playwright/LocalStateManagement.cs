using System.Text;
using System.Text.Json;
using NUnit.Framework.Interfaces;

namespace Contoso.Playwright;

public class SynqraPageTest : PageTest
{
	private readonly List<string> _browserDiagnostics = [];
	private readonly List<string> _contextDiagnostics = [];
	private bool _diagnosticsAttached;
	private bool _contextDiagnosticsAttached;
	private Microsoft.Playwright.ICDPSession? _cdpSession;
	private string? _artifactsDirectory;

	protected string BaseUrl { get; }

	protected virtual string RelativePath => string.Empty;

	protected IReadOnlyList<string> BrowserDiagnostics => _browserDiagnostics;

	protected string ArtifactsDirectory => _artifactsDirectory ?? throw new InvalidOperationException("The test artifacts directory has not been initialized.");

	public SynqraPageTest()
	{
		var baseUrl = Environment.GetEnvironmentVariable("SYNQRA_CONTOSO_TEST_HOST");
		if (string.IsNullOrEmpty(baseUrl))
		{
			baseUrl = "http://localhost:5063/";
		}
		BaseUrl = baseUrl;
	}

	[SetUp]
	[Property("CI", "false")]
	[Explicit]
	public async Task SetUp()
	{
		_artifactsDirectory = CreateArtifactsDirectory();
		_browserDiagnostics.Clear();
		_contextDiagnostics.Clear();

		AttachContextDiagnostics();
		AttachBrowserDiagnostics();
		await AttachChromiumProtocolDiagnosticsAsync();
		await Context.Tracing.StartAsync(new()
		{
			Name = TestContext.CurrentContext.Test.Name,
			Screenshots = true,
			Snapshots = true,
			Sources = true
		});

		var pageUrl = new Uri(new Uri(BaseUrl, UriKind.Absolute), RelativePath);
		_browserDiagnostics.Add($"goto:{pageUrl}");
		await Page.GotoAsync(pageUrl.ToString(), new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded });
	}

	[TearDown]
	public async Task TearDownAsync()
	{
		if (_artifactsDirectory is null)
		{
			return;
		}

		if (_cdpSession is not null)
		{
			try
			{
				await _cdpSession.DetachAsync();
			}
			catch
			{
				// Best-effort cleanup for debugging instrumentation.
			}

			_cdpSession = null;
		}

		var outcome = TestContext.CurrentContext.Result.Outcome.Status;
		var shouldPreserveArtifacts = outcome is TestStatus.Failed or TestStatus.Inconclusive;
		await PersistArtifactsAsync(shouldPreserveArtifacts);
	}

	protected async Task FailWithBrowserDiagnosticsAsync(Exception ex)
	{
		Assert.Fail(await BuildFailureReportAsync(ex));
	}

	protected async Task<string> BuildFailureReportAsync(Exception ex)
	{
		var builder = new StringBuilder();
		builder.AppendLine(ex.Message);
		builder.AppendLine();
		builder.AppendLine("Browser diagnostics:");
		builder.AppendLine(FormatBrowserDiagnostics());
		builder.AppendLine();
		builder.AppendLine("Artifacts:");
		builder.AppendLine(ArtifactsDirectory);

		try
		{
			builder.AppendLine();
			builder.AppendLine("Page URL:");
			builder.AppendLine(Page.Url);
		}
		catch
		{
			builder.AppendLine();
			builder.AppendLine("Page URL:");
			builder.AppendLine("<unavailable>");
		}

		try
		{
			builder.AppendLine();
			builder.AppendLine("Page title:");
			builder.AppendLine(await Page.TitleAsync());
		}
		catch
		{
			builder.AppendLine();
			builder.AppendLine("Page title:");
			builder.AppendLine("<unavailable>");
		}

		try
		{
			var content = await Page.ContentAsync();
			var contentPreview = content.Length <= 4000 ? content : content[..4000];
			builder.AppendLine();
			builder.AppendLine("Content preview:");
			builder.AppendLine(contentPreview);
		}
		catch
		{
			builder.AppendLine();
			builder.AppendLine("Content preview:");
			builder.AppendLine("<unavailable>");
		}

		return builder.ToString();
	}

	protected string FormatBrowserDiagnostics()
	{
		var allDiagnostics = _contextDiagnostics.Concat(_browserDiagnostics).ToArray();
		return allDiagnostics.Length == 0
			? "No browser diagnostics were captured."
			: string.Join(Environment.NewLine, allDiagnostics);
	}

	private void AttachContextDiagnostics()
	{
		if (_contextDiagnosticsAttached)
		{
			return;
		}

		Context.Console += (_, message) => _contextDiagnostics.Add($"context-console:{message.Type}: {message.Text}");
		Context.WebError += (_, error) => _contextDiagnostics.Add($"context-weberror: {error.Error}");
		Context.RequestFailed += (_, request) => _contextDiagnostics.Add($"context-requestfailed: {request.Method} {request.Url} :: {request.Failure}");
		Context.Response += (_, response) =>
		{
			if (response.Status >= 400)
			{
				_contextDiagnostics.Add($"context-response:{response.Status}: {response.Request.Method} {response.Url}");
			}
		};
		Context.Page += (_, page) => _contextDiagnostics.Add($"context-page:{page.Url}");

		_contextDiagnosticsAttached = true;
	}

	private void AttachBrowserDiagnostics()
	{
		if (_diagnosticsAttached)
		{
			return;
		}

		Page.Console += (_, message) =>
		{
			_browserDiagnostics.Add($"console:{message.Type}: {message.Text}");
		};
		Page.PageError += (_, message) => _browserDiagnostics.Add($"pageerror: {message}");
		Page.RequestFailed += (_, request) => _browserDiagnostics.Add($"requestfailed: {request.Method} {request.Url} :: {request.Failure}");
		Page.Response += (_, response) =>
		{
			if (response.Status >= 400)
			{
				_browserDiagnostics.Add($"response:{response.Status}: {response.Request.Method} {response.Url}");
			}
		};
		Page.FrameNavigated += (_, frame) =>
		{
			if (frame == Page.MainFrame)
			{
				_browserDiagnostics.Add($"framenavigated:{frame.Url}");
			}
		};
		Page.Crash += (_, _) => _browserDiagnostics.Add("pagecrash");

		_diagnosticsAttached = true;
	}

	private async Task AttachChromiumProtocolDiagnosticsAsync()
	{
		if (Browser.BrowserType.Name != "chromium")
		{
			return;
		}

		_cdpSession = await Context.NewCDPSessionAsync(Page);
		HookCdpEvent("Runtime.consoleAPICalled");
		HookCdpEvent("Runtime.exceptionThrown");
		HookCdpEvent("Log.entryAdded");
		HookCdpEvent("Network.loadingFailed");
		HookCdpEvent("Page.javascriptDialogOpening");
		HookCdpEvent("Inspector.targetCrashed");

		await _cdpSession.SendAsync("Runtime.enable");
		await _cdpSession.SendAsync("Log.enable");
		await _cdpSession.SendAsync("Network.enable");
		await _cdpSession.SendAsync("Page.enable");
	}

	private void HookCdpEvent(string eventName)
	{
		if (_cdpSession is null)
		{
			return;
		}

		_cdpSession.Event(eventName).OnEvent += (_, payload) =>
		{
			_browserDiagnostics.Add($"cdp:{eventName}: {FormatJsonPayload(payload)}");
		};
	}

	private async Task PersistArtifactsAsync(bool shouldPreserveArtifacts)
	{
		var diagnosticsPath = Path.Combine(ArtifactsDirectory, "browser-diagnostics.txt");
		await File.WriteAllTextAsync(diagnosticsPath, FormatBrowserDiagnostics());

		var tracePath = Path.Combine(ArtifactsDirectory, "trace.zip");
		await Context.Tracing.StopAsync(new() { Path = shouldPreserveArtifacts ? tracePath : null });

		if (!shouldPreserveArtifacts)
		{
			if (File.Exists(diagnosticsPath))
			{
				File.Delete(diagnosticsPath);
			}

			if (File.Exists(tracePath))
			{
				File.Delete(tracePath);
			}

			if (Directory.Exists(ArtifactsDirectory) && !Directory.EnumerateFileSystemEntries(ArtifactsDirectory).Any())
			{
				Directory.Delete(ArtifactsDirectory);
			}

			return;
		}

		try
		{
			var htmlPath = Path.Combine(ArtifactsDirectory, "page.html");
			await File.WriteAllTextAsync(htmlPath, await Page.ContentAsync());
		}
		catch
		{
			// Best-effort capture for debugging failed browser tests.
		}

		try
		{
			var screenshotPath = Path.Combine(ArtifactsDirectory, "page.png");
			await Page.ScreenshotAsync(new() { Path = screenshotPath, FullPage = true });
		}
		catch
		{
			// Best-effort capture for debugging failed browser tests.
		}

		try
		{
			var diagnosticsJsonPath = Path.Combine(ArtifactsDirectory, "browser-diagnostics.json");
			await File.WriteAllTextAsync(diagnosticsJsonPath, BuildDiagnosticsJson());
		}
		catch
		{
			// Best-effort capture for debugging failed browser tests.
		}
	}

	private static string CreateArtifactsDirectory()
	{
		var testName = TestContext.CurrentContext.Test.FullName ?? TestContext.CurrentContext.Test.Name;
		var invalidCharacters = Path.GetInvalidFileNameChars();
		var safeName = string.Concat(testName.Select(ch => invalidCharacters.Contains(ch) ? '_' : ch));
		var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "playwright-artifacts", safeName);
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
		Directory.CreateDirectory(path);
		return path;
	}

	private string BuildDiagnosticsJson()
	{
		var payload = new
		{
			ContextDiagnostics = _contextDiagnostics,
			PageDiagnostics = _browserDiagnostics
		};
		return System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
		{
			WriteIndented = true
		});
	}

	private static string FormatJsonPayload(JsonElement? payload)
	{
		if (payload is null)
		{
			return "<null>";
		}

		return payload.Value.GetRawText();
	}
}

[Parallelizable(ParallelScope.Self)]
public class LocalStateManagement : SynqraPageTest
{
	[Test]
	[Property("CI", "false")]
	[Category("Performance")]
	[Explicit]
	public async Task Should_add_elements_into_collection()
	{
		var newItem1 = Guid.NewGuid().ToString("N");
		await Page.GetByTestId("itemName").FillAsync($"Item {newItem1}");
		await Page.GetByTestId("btnAdd").ClickAsync();

		var newItem2 = Guid.NewGuid().ToString("N");
		await Page.GetByTestId("itemName").FillAsync($"Item {newItem2}");
		await Page.GetByTestId("btnAdd").ClickAsync();

		await Expect(Page.GetByText($"Item {newItem1}")).ToBeEnabledAsync();
		await Expect(Page.GetByText($"Item {newItem2}")).ToBeEnabledAsync();
	}
}
