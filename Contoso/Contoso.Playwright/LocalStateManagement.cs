namespace Contoso.Playwright;

public class SynqraPageTest : PageTest
{
	public string BaseUrl;

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
	public async Task SetUp()
	{
		await Page.GotoAsync(BaseUrl);
		await Expect(Page).ToHaveTitleAsync(new Regex("Synqra Contoso Playwright"));
	}
}

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class LocalStateManagement : SynqraPageTest
{
	[Test]
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
