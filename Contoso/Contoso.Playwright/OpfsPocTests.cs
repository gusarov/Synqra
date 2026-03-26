namespace Contoso.Playwright;

[Parallelizable(ParallelScope.Self)]
public class OpfsPocTests : SynqraPageTest
{
	protected override string RelativePath => "opfs-poc";

	[Test]
	[Property("CI", "false")]
	[Explicit]
	public async Task Should_complete_opfs_suites_in_chromium()
	{
		Assume.That(Browser.BrowserType.Name, Is.EqualTo("chromium"), "The OPFS PoC assertions are scoped to Chromium.");

		try
		{
			await Expect(Page.GetByTestId("opfs-poc-page")).ToBeVisibleAsync(new() { Timeout = 30000 });
			await Expect(Page.GetByTestId("opfs-poc-completed-at")).ToBeVisibleAsync(new() { Timeout = 30000 });
			await Expect(Page.GetByTestId("opfs-poc-run-state")).ToHaveTextAsync("Success", new() { Timeout = 30000 });
			await Expect(Page.GetByTestId("opfs-suite-async-status")).ToHaveTextAsync("Passed", new() { Timeout = 30000 });
			await Expect(Page.GetByTestId("opfs-suite-sync-worker-status")).ToHaveTextAsync("Passed", new() { Timeout = 30000 });
			await Expect(Page.GetByTestId("opfs-poc-error")).ToHaveCountAsync(0, new() { Timeout = 30000 });
		}
		catch (Exception ex)
		{
			await FailWithBrowserDiagnosticsAsync(ex);
		}
	}
}
