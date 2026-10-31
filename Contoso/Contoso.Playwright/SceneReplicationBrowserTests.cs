using System.Net.Http.Json;
using Microsoft.Playwright;
using Microsoft.Playwright.TestAdapter;

namespace Contoso.Playwright;

/// <summary>
/// A scene replicated between real browsers and the Contoso master (plans/snapshot.md), each browser a real WASM client over
/// IndexedDB. Two paths, each on its own, then together:
/// <list type="bullet">
/// <item>creation: a browser makes a new stream and a scene in it, and the master ends up holding the same scene;</item>
/// <item>first load: a browser that holds nothing of an existing stream loads its state from a snapshot, not its history, and its
/// next load starts from what it stored;</item>
/// <item>both: one browser creates a scene, a second browser (another process, by default Chromium again; set
/// <c>SYNQRA_SECOND_BROWSER</c> to <c>firefox</c> or <c>webkit</c> where those are installed) loads it on its first load, and edits
/// then flow both ways.</item>
/// </list>
/// A scene is compared as text: the master and the page describe it the same way (<c>ContosoScene.Describe</c>).
/// </summary>
[TestFixture]
[Category("Replication")]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)] // each test owns its browser contexts and streams: they run side by side
public class SceneReplicationBrowserTests : BrowserTest
{
	readonly HttpClient _server = new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable("SYNQRA_CONTOSO_TEST_HOST") is { Length: > 0 } host ? host : "http://localhost:5063/") };

	IBrowser? _secondBrowser;

	[TearDown]
	public async Task CloseAsync()
	{
		_server.Dispose();
		if (_secondBrowser is not null)
		{
			await _secondBrowser.DisposeAsync();
		}
	}

	// ---------------------------------------------------------------- creation

	[Test]
	public async Task A_scene_a_browser_creates_in_a_new_stream_reaches_the_master()
	{
		var page = await (await NewContext()).NewPageAsync();
		var stream = await CreateStreamHereAsync(page);

		await BuildAsync(page, "made in the browser", 12);
		var scene = await WaitForNodesAsync(page, 11); // one of the twelve is deleted by the build's edits

		await WaitForServerSceneAsync(stream, scene);
		Assert.That(await ReadCounterAsync(page, "snapshots"), Is.EqualTo(0), "a new stream has no state on the master to load");
	}

	// ---------------------------------------------------------------- first load

	[Test]
	public async Task A_fresh_browser_loads_an_existing_scene_on_its_first_load_from_a_snapshot_and_keeps_it_on_reload()
	{
		var stream = Guid.NewGuid();
		await BuildOnServerAsync(stream, "made on the server", 30);
		var expected = await ReadServerSceneAsync(stream);

		var page = await (await NewContext()).NewPageAsync();
		await OpenAsync(page, stream);

		await WaitForSceneAsync(page, expected);
		Assert.That(await ReadCounterAsync(page, "snapshots"), Is.EqualTo(1), "a cold browser loads the stream's state");
		Assert.That(await ReadCounterAsync(page, "frames"), Is.EqualTo(0), "and none of its history");

		// The next load starts from the snapshot it stored, and takes only what the master wrote meanwhile.
		await RenameOnServerAsync(stream, "renamed while the page was away");
		await page.ReloadAsync();
		await WaitReadyAsync(page);
		await WaitForSceneAsync(page, await ReadServerSceneAsync(stream));
		Assert.That(await ReadCounterAsync(page, "snapshots"), Is.EqualTo(0), "a warm browser resumes from its watermark");
	}

	// ---------------------------------------------------------------- both

	[Test]
	public async Task A_scene_made_in_one_browser_loads_into_another_on_its_first_load_and_edits_flow_both_ways()
	{
		var creator = await (await NewContext()).NewPageAsync();
		var stream = await CreateStreamHereAsync(creator);
		await BuildAsync(creator, "made by the creator", 12);
		var scene = await WaitForNodesAsync(creator, 11);
		await WaitForServerSceneAsync(stream, scene);

		// Another browser, never connected before, as a user signing in on a second device.
		var other = await (await NewSecondBrowserContextAsync()).NewPageAsync();
		await OpenAsync(other, stream);
		await WaitForSceneAsync(other, scene);
		Assert.That(await ReadCounterAsync(other, "snapshots"), Is.EqualTo(1), "the second browser loads the scene's state");

		await RenameAsync(other, "renamed in the second browser");
		await WaitForSceneAsync(creator, await ReadSceneAsync(other, "renamed in the second browser"));

		await creator.GetByTestId("btnLink").ClickAsync();
		var linked = await ReadSceneAsync(creator, ":9.5");
		await WaitForSceneAsync(other, linked);
		await WaitForServerSceneAsync(stream, linked);
	}

	// ---------------------------------------------------------------- pages

	string ComposeSceneUrl(Guid? stream) => new Uri(_server.BaseAddress!, stream is Guid s ? $"scene?stream={s:D}" : "scene").ToString();

	async Task OpenAsync(IPage page, Guid stream)
	{
		await page.GotoAsync(ComposeSceneUrl(stream));
		await WaitReadyAsync(page);
	}

	async Task<Guid> CreateStreamHereAsync(IPage page)
	{
		await page.GotoAsync(ComposeSceneUrl(null));
		await Expect(page.GetByTestId("btnCreateStream")).ToBeVisibleAsync(new() { Timeout = 60_000 }); // a cold WASM start
		await page.GetByTestId("btnCreateStream").ClickAsync();
		await page.WaitForURLAsync(new Regex(@"scene\?stream="));
		await WaitReadyAsync(page);
		return Guid.Parse(await page.GetByTestId("stream").InnerTextAsync());
	}

	async Task WaitReadyAsync(IPage page)
	{
		await Expect(page.GetByTestId("ready")).ToBeVisibleAsync(new() { Timeout = 60_000 }); // a cold WASM start
		await Expect(page.GetByTestId("online")).ToHaveTextAsync("online", new() { Timeout = 30_000 });
	}

	async Task BuildAsync(IPage page, string prefix, int nodes)
	{
		await page.GetByTestId("prefix").FillAsync(prefix);
		await page.GetByTestId("nodes").FillAsync(nodes.ToString());
		await page.GetByTestId("btnBuild").ClickAsync();
	}

	async Task RenameAsync(IPage page, string name)
	{
		await page.GetByTestId("renameTo").FillAsync(name);
		await page.GetByTestId("btnRename").ClickAsync();
	}

	async Task<string> ReadSceneAsync(IPage page) => Normalize(await page.GetByTestId("scene").InnerTextAsync());

	/// <summary>The page's scene once it shows <paramref name="marker"/>: a write made on it has been applied.</summary>
	async Task<string> ReadSceneAsync(IPage page, string marker)
	{
		await Expect(page.GetByTestId("scene")).ToContainTextAsync(marker, new() { Timeout = 30_000 });
		return await ReadSceneAsync(page);
	}

	async Task<long> ReadCounterAsync(IPage page, string counter) => long.Parse(await page.GetByTestId(counter).InnerTextAsync());

	async Task<string> WaitForNodesAsync(IPage page, int nodes)
	{
		await Expect(page.GetByTestId("node-count")).ToHaveTextAsync(nodes.ToString(), new() { Timeout = 60_000 });
		await Expect(page.GetByTestId("error")).ToBeEmptyAsync();
		return await ReadSceneAsync(page);
	}

	async Task WaitForSceneAsync(IPage page, string expected)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
		var actual = await ReadSceneAsync(page);
		while (actual != expected && DateTime.UtcNow < deadline)
		{
			await Task.Delay(250);
			actual = await ReadSceneAsync(page);
		}
		Assert.That(actual, Is.EqualTo(expected), "the page's scene");
	}

	/// <summary>A browser of its own (another process), never the fixture's: nothing it stores is shared with the first.</summary>
	async Task<IBrowserContext> NewSecondBrowserContextAsync()
	{
		var kind = Environment.GetEnvironmentVariable("SYNQRA_SECOND_BROWSER") is { Length: > 0 } configured ? configured : "chromium";
		// The same kind as the fixture's launches as the fixture's does (its run settings may name the executable); another kind as installed.
		var options = kind == PlaywrightSettingsProvider.BrowserName ? PlaywrightSettingsProvider.LaunchOptions : new BrowserTypeLaunchOptions();
		_secondBrowser = await Playwright[kind].LaunchAsync(options);
		return await _secondBrowser.NewContextAsync();
	}

	string Normalize(string scene) => scene.Replace("\r", "").Trim();

	// ---------------------------------------------------------------- the master

	async Task BuildOnServerAsync(Guid stream, string prefix, int nodes)
	{
		using var response = await _server.PostAsJsonAsync($"api/streams/{stream:D}/scene", new { prefix, nodes });
		response.EnsureSuccessStatusCode();
	}

	async Task RenameOnServerAsync(Guid stream, string name)
	{
		using var response = await _server.PostAsJsonAsync($"api/streams/{stream:D}/scene/rename", new { name });
		response.EnsureSuccessStatusCode();
	}

	async Task<string> ReadServerSceneAsync(Guid stream) => Normalize(await _server.GetStringAsync($"api/streams/{stream:D}/scene"));

	async Task WaitForServerSceneAsync(Guid stream, string expected)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
		var actual = await ReadServerSceneAsync(stream);
		while (actual != expected && DateTime.UtcNow < deadline)
		{
			await Task.Delay(250);
			actual = await ReadServerSceneAsync(stream);
		}
		Assert.That(actual, Is.EqualTo(expected), $"the master's scene of {stream}");
	}
}
