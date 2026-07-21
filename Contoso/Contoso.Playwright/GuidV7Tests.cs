namespace Contoso.Playwright;

// Browser-level regression guard for the v7 sub-ms fix (GuidExtensions). Unlike the other Playwright
// tests here this one is intentionally NOT [Explicit] and NOT CI=false: it must run in the PR gate so
// that removing the entropy fallback fails CI. In a real browser DateTime.UtcNow is clamped to whole
// milliseconds, so without that fallback every fresh-generator id reads group3 "7000".
[Parallelizable(ParallelScope.Self)]
public class GuidV7Tests : SynqraPageTest
{
	protected override string RelativePath => "guid-v7";

	[Test]
	public async Task V7_guids_are_not_constant_7000_in_wasm()
	{
		try
		{
			await Expect(Page.GetByTestId("guid-v7-page")).ToBeVisibleAsync(new() { Timeout = 30000 });
			// sanity: we really are exercising the browser clock, not a native host
			await Expect(Page.GetByTestId("guid-v7-env")).ToHaveTextAsync("Browser/WASM", new() { Timeout = 30000 });

			await Page.GetByTestId("guid-v7-generate").ClickAsync();
			await Expect(Page.GetByTestId("guid-v7-count")).ToHaveTextAsync("16", new() { Timeout = 30000 });

			// the bug: sub-ms field 0 with no entropy fallback => every fresh-generator id is group3 "7000"
			await Expect(Page.GetByTestId("guid-v7-all-7000")).ToHaveTextAsync("false", new() { Timeout = 30000 });
			// and they must actually vary (entropy fills the field)
			await Expect(Page.GetByTestId("guid-v7-distinct-group3")).Not.ToHaveTextAsync("1", new() { Timeout = 30000 });
		}
		catch (Exception ex)
		{
			await FailWithBrowserDiagnosticsAsync(ex);
		}
	}
}
