using System.Net.Http.Json;
using Microsoft.Playwright;

namespace Contoso.Playwright;

/// <summary>
/// Seeding in a real browser against the Contoso master (docs/model.md §10): a stream is seeded by its creator only. The master
/// seeds the streams it created (<c>POST /api/streams</c>); a browser seeds the streams it created here. A browser that opens any
/// other stream, however empty its IndexedDB, online or not, after a wipe, a reload, or a copied URL, writes nothing at all.
/// Each test runs in fresh browser contexts: an empty IndexedDB and no creation record.
/// </summary>
[TestFixture]
[Category("Seeding")]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)] // each test owns its browser contexts and streams: they run side by side
public class StreamSeedingBrowserTests : BrowserTest
{
	// Long enough for a browser that was going to write a seed to have written and sent it.
	readonly TimeSpan _quietPeriod = TimeSpan.FromSeconds(2.5);

	readonly Regex _replicationSocket = new("/api/synqra/ws");

	readonly HttpClient _server = new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable("SYNQRA_CONTOSO_TEST_HOST") is { Length: > 0 } host ? host : "http://localhost:5063/") };

	[TearDown]
	public void DisposeServerClient() => _server.Dispose();

	// ---------------------------------------------------------------- the creators seed, once

	[Test]
	public async Task Should_show_the_masters_seed_once_in_a_fresh_browser()
	{
		var stream = await CreateServerStreamAsync();
		var page = await OpenFreshAsync(stream);

		await ExpectOnlineAsync(page);
		await ExpectWelcomesAsync(page, 1);
		await HoldsAsync(page, welcomes: 1);
		await ExpectServerAsync(stream, welcomes: 1);
	}

	[Test]
	[TestCase(4)]
	[TestCase(10)]
	public async Task Should_show_one_seed_in_each_of_many_fresh_browsers_opening_a_new_stream_at_once(int browsers)
	{
		var stream = await CreateServerStreamAsync();

		var pages = await Task.WhenAll(Enumerable.Range(0, browsers).Select(_ => OpenFreshAsync(stream)));

		foreach (var page in pages)
		{
			await ExpectWelcomesAsync(page, 1);
		}
		await Task.Delay(_quietPeriod);
		foreach (var page in pages)
		{
			await ExpectWelcomesAsync(page, 1);
		}
		await ExpectServerAsync(stream, welcomes: 1);
	}

	[Test]
	public async Task Should_keep_one_seed_across_reloads()
	{
		var stream = await CreateServerStreamAsync();
		var page = await OpenFreshAsync(stream);
		await ExpectWelcomesAsync(page, 1);

		for (var i = 0; i < 5; i++)
		{
			await ReloadAsync(page);
			await ExpectOnlineAsync(page);
			await ExpectWelcomesAsync(page, 1);
		}
		await HoldsAsync(page, welcomes: 1);
		await ExpectServerAsync(stream, welcomes: 1);
	}

	[Test]
	public async Task Should_keep_one_seed_after_the_browser_log_is_wiped_again_and_again()
	{
		var stream = await CreateServerStreamAsync();
		var page = await OpenFreshAsync(stream);
		await ExpectWelcomesAsync(page, 1);

		for (var i = 0; i < 4; i++)
		{
			await WipeBrowserStorageAsync(page); // a Force Resync, a cleared browser: an empty log and a cold cursor
			await ReloadAsync(page);
			await ExpectOnlineAsync(page);
			await ExpectWelcomesAsync(page, 1);
		}
		await HoldsAsync(page, welcomes: 1);
		await ExpectServerAsync(stream, welcomes: 1);
	}

	[Test]
	public async Task Should_seed_a_stream_created_in_this_browser_while_the_server_is_unreachable_and_deliver_that_seed_once()
	{
		var creatorContext = await NewContext();
		var creator = await creatorContext.NewPageAsync();
		await SilenceServerAsync(creator);
		await OpenAsync(creator, null);

		var stream = await CreateStreamHereAsync(creator);
		await ExpectWelcomesAsync(creator, 1); // seeded here, with no server at all
		await Expect(creator.GetByTestId("online")).ToHaveTextAsync("offline");
		await ExpectServerAsync(stream, events: 0);

		var synced = await creatorContext.NewPageAsync(); // the server is reachable again: the creator's next load sends its seed
		await OpenAsync(synced, stream);
		await ExpectOnlineAsync(synced);
		await WaitForServerAsync(stream, welcomes: 1);
		await ExpectWelcomesAsync(synced, 1);

		var later = await OpenFreshAsync(stream);
		await ExpectWelcomesAsync(later, 1);
		await HoldsAsync(later, welcomes: 1);
		await ExpectServerAsync(stream, welcomes: 1);
	}

	[Test]
	public async Task Should_seed_a_stream_created_in_this_browser_online_once()
	{
		var page = await (await NewContext()).NewPageAsync();
		await OpenAsync(page, null);

		var stream = await CreateStreamHereAsync(page);

		await ExpectOnlineAsync(page);
		await ExpectWelcomesAsync(page, 1);
		await WaitForServerAsync(stream, welcomes: 1);
		await ReloadAsync(page);
		await HoldsAsync(page, welcomes: 1);
		await ExpectServerAsync(stream, welcomes: 1);
	}

	// ---------------------------------------------------------------- no one else seeds, ever

	[Test]
	public async Task Should_not_seed_a_stream_nobody_created()
	{
		var stream = Guid.NewGuid();
		var page = await OpenFreshAsync(stream);

		await ExpectOnlineAsync(page);
		await HoldsAsync(page, welcomes: 0, items: 0);
		await ExpectServerAsync(stream, events: 0);
	}

	[Test]
	public async Task Should_send_nothing_but_its_hello_for_a_stream_nobody_created()
	{
		var stream = Guid.NewGuid();
		var page = await (await NewContext()).NewPageAsync();
		var sent = RecordSentFrames(page);

		await OpenAsync(page, stream);
		await ExpectOnlineAsync(page);
		await Task.Delay(_quietPeriod);

		Assert.That(sent, Has.Count.EqualTo(1), "a browser that opens a stream it did not create sends its HELLO and nothing else");
		await ExpectServerAsync(stream, events: 0);
	}

	[Test]
	public async Task Should_send_nothing_but_its_hello_for_a_server_stream_it_was_given()
	{
		var stream = await CreateServerStreamAsync();
		var page = await (await NewContext()).NewPageAsync();
		var sent = RecordSentFrames(page);

		await OpenAsync(page, stream);
		await ExpectWelcomesAsync(page, 1);
		await Task.Delay(_quietPeriod);

		Assert.That(sent, Has.Count.EqualTo(1), "the seed came from the master: the browser sends no event of its own");
		await ExpectServerAsync(stream, welcomes: 1);
	}

	[Test]
	[TestCase(4)]
	[TestCase(10)]
	public async Task Should_not_seed_in_any_of_many_fresh_browsers_opening_a_stream_nobody_created(int browsers)
	{
		var stream = Guid.NewGuid();

		var pages = await Task.WhenAll(Enumerable.Range(0, browsers).Select(_ => OpenFreshAsync(stream)));

		await Task.Delay(_quietPeriod);
		foreach (var page in pages)
		{
			await ExpectOnlineAsync(page);
			await ExpectWelcomesAsync(page, 0);
		}
		await ExpectServerAsync(stream, events: 0);
	}

	[Test]
	public async Task Should_not_seed_a_stream_nobody_created_through_reloads_and_wipes()
	{
		var stream = Guid.NewGuid();
		var page = await OpenFreshAsync(stream);

		for (var i = 0; i < 6; i++)
		{
			if (i % 2 == 1)
			{
				await WipeBrowserStorageAsync(page);
			}
			await ReloadAsync(page);
			await ExpectOnlineAsync(page);
			await ExpectWelcomesAsync(page, 0);
		}
		await HoldsAsync(page, welcomes: 0, items: 0);
		await ExpectServerAsync(stream, events: 0);
	}

	[Test]
	public async Task Should_not_seed_a_server_stream_that_already_holds_history()
	{
		var stream = await CreateServerStreamAsync();
		await WriteOnServerAsync(stream, "written over REST before any browser came");

		var page = await OpenFreshAsync(stream);

		await Expect(page.GetByTestId("item-count")).ToHaveTextAsync("1");
		await HoldsAsync(page, welcomes: 0, items: 1);
		await ExpectServerAsync(stream, welcomes: 0, events: 2);
	}

	[Test]
	public async Task Should_not_seed_a_new_server_stream_while_its_server_is_silent()
	{
		var stream = await CreateServerStreamAsync();
		var context = await NewContext();
		var page = await context.NewPageAsync();
		await SilenceServerAsync(page);

		await OpenAsync(page, stream);

		await Expect(page.GetByTestId("online")).ToHaveTextAsync("offline");
		await HoldsAsync(page, welcomes: 0, items: 0);
		await ExpectServerAsync(stream, events: 0);

		var connected = await context.NewPageAsync(); // the server answers again: the seed comes from the master, once
		await OpenAsync(connected, stream);
		await ExpectWelcomesAsync(connected, 1);
		await HoldsAsync(connected, welcomes: 1);
		await ExpectServerAsync(stream, welcomes: 1);
	}

	[Test]
	public async Task Should_not_seed_while_every_connection_is_closed_on_it()
	{
		var stream = await CreateServerStreamAsync();
		var page = await (await NewContext()).NewPageAsync();
		await page.RouteWebSocketAsync(_replicationSocket, async ws =>
		{
			await Task.Delay(50);
			await ws.CloseAsync(new() { Code = 1011, Reason = "refused" }); // closed on it, again and again, as it reconnects
		});

		await OpenAsync(page, stream);

		await Expect(page.GetByTestId("online")).ToHaveTextAsync("offline");
		await HoldsAsync(page, welcomes: 0, items: 0);
		await ExpectServerAsync(stream, events: 0);
	}

	[Test]
	public async Task Should_not_seed_a_stream_nobody_created_while_its_server_is_silent()
	{
		var stream = Guid.NewGuid();
		var page = await (await NewContext()).NewPageAsync();
		await SilenceServerAsync(page);

		await OpenAsync(page, stream);

		await HoldsAsync(page, welcomes: 0, items: 0);
		await ExpectServerAsync(stream, events: 0);
	}

	[Test]
	public async Task Should_not_seed_another_browsers_new_stream_before_or_after_its_creator_syncs()
	{
		var creatorContext = await NewContext();
		var creator = await creatorContext.NewPageAsync();
		await SilenceServerAsync(creator);
		await OpenAsync(creator, null);
		var stream = await CreateStreamHereAsync(creator); // made offline: the server has never heard of it

		var visitor = await OpenFreshAsync(stream); // the copied URL, in another browser
		await ExpectOnlineAsync(visitor);
		await HoldsAsync(visitor, welcomes: 0, items: 0);
		await ExpectServerAsync(stream, events: 0);

		var synced = await creatorContext.NewPageAsync();
		await OpenAsync(synced, stream);
		await WaitForServerAsync(stream, welcomes: 1);

		await ReloadAsync(visitor);
		await ExpectWelcomesAsync(visitor, 1); // the creator's seed, not one of its own
		await HoldsAsync(visitor, welcomes: 1);
		await ExpectServerAsync(stream, welcomes: 1);
	}

	[Test]
	public async Task Should_refuse_a_replayed_creation_record_for_a_stream_that_holds_events()
	{
		var page = await (await NewContext()).NewPageAsync();
		await OpenAsync(page, null);
		var stream = await CreateStreamHereAsync(page);
		await ExpectWelcomesAsync(page, 1);
		await WaitForServerAsync(stream, welcomes: 1);

		await page.EvaluateAsync("key => sessionStorage.setItem(key, 'replayed')", ComposeCreationRecordKey(stream));
		await ReloadAsync(page);

		await HoldsAsync(page, welcomes: 1);
		await ExpectServerAsync(stream, welcomes: 1);
		Assert.That(await page.EvaluateAsync<string?>("key => sessionStorage.getItem(key)", ComposeCreationRecordKey(stream)), Is.Null, "the record is spent");
	}

	[Test]
	public async Task Should_not_seed_a_new_stream_again_from_another_tab_of_its_creators_browser()
	{
		var context = await NewContext();
		var creator = await context.NewPageAsync();
		await SilenceServerAsync(creator);
		await OpenAsync(creator, null);
		var stream = await CreateStreamHereAsync(creator); // the creation record lives in this tab only

		var secondTab = await context.NewPageAsync(); // the same browser: the same IndexedDB log, its own sessionStorage
		await SilenceServerAsync(secondTab);
		await OpenAsync(secondTab, stream);

		await HoldsAsync(secondTab, welcomes: 1); // the creator's seed, read from the shared log
		await HoldsAsync(creator, welcomes: 1);

		var synced = await context.NewPageAsync();
		await OpenAsync(synced, stream);
		await WaitForServerAsync(stream, welcomes: 1);
		await HoldsAsync(synced, welcomes: 1);
		await ExpectServerAsync(stream, welcomes: 1);
	}

	[Test]
	public async Task Should_not_seed_a_stream_when_the_creation_record_names_another_one()
	{
		var stream = Guid.NewGuid();
		var page = await (await NewContext()).NewPageAsync();
		await OpenAsync(page, null);
		await page.EvaluateAsync("key => sessionStorage.setItem(key, 'someone else')", ComposeCreationRecordKey(Guid.NewGuid()));

		await page.GotoAsync(ComposeSeedingUrl(stream));
		await WaitReadyAsync(page);

		await ExpectOnlineAsync(page);
		await HoldsAsync(page, welcomes: 0, items: 0);
		await ExpectServerAsync(stream, events: 0);
	}

	[Test]
	public async Task Should_not_seed_the_streams_a_browser_merely_visits_in_a_row()
	{
		var page = await (await NewContext()).NewPageAsync();
		var visited = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

		foreach (var stream in visited)
		{
			await page.GotoAsync(ComposeSeedingUrl(stream));
			await WaitReadyAsync(page);
			await ExpectOnlineAsync(page);
			await ExpectWelcomesAsync(page, 0);
		}
		await Task.Delay(_quietPeriod);
		foreach (var stream in visited)
		{
			await ExpectServerAsync(stream, events: 0);
		}
	}

	// ---------------------------------------------------------------- helpers

	async Task<Guid> CreateServerStreamAsync()
	{
		using var response = await _server.PostAsync("api/streams", null);
		response.EnsureSuccessStatusCode();
		var created = await response.Content.ReadFromJsonAsync<StreamCreated>() ?? throw new InvalidOperationException("The server answered no stream.");
		return created.StreamId;
	}

	async Task WriteOnServerAsync(Guid stream, string itemName)
	{
		using var response = await _server.PostAsJsonAsync($"api/streams/{stream:D}/items", new { itemName });
		response.EnsureSuccessStatusCode();
	}

	async Task<StreamLog> ReadServerLogAsync(Guid stream)
		=> await _server.GetFromJsonAsync<StreamLog>($"api/streams/{stream:D}/log") ?? throw new InvalidOperationException("The server answered no log.");

	async Task ExpectServerAsync(Guid stream, int? welcomes = null, int? events = null)
	{
		var log = await ReadServerLogAsync(stream);
		if (welcomes is int w)
		{
			Assert.That(log.Welcomes, Is.EqualTo(w), $"Welcome items in the server log of {stream}");
		}
		if (events is int e)
		{
			Assert.That(log.Events, Is.EqualTo(e), $"events in the server log of {stream}");
		}
	}

	async Task WaitForServerAsync(Guid stream, int welcomes)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		var log = await ReadServerLogAsync(stream);
		while (log.Welcomes < welcomes && DateTime.UtcNow < deadline)
		{
			await Task.Delay(200);
			log = await ReadServerLogAsync(stream);
		}
		Assert.That(log.Welcomes, Is.EqualTo(welcomes), $"Welcome items reaching the server log of {stream}");
	}

	string ComposeSeedingUrl(Guid? stream) => new Uri(_server.BaseAddress!, stream is Guid s ? $"seeding?stream={s:D}" : "seeding").ToString();

	string ComposeCreationRecordKey(Guid stream) => $"synqra-contoso-created-{stream:N}";

	async Task<IPage> OpenFreshAsync(Guid stream)
	{
		var page = await (await NewContext()).NewPageAsync();
		await OpenAsync(page, stream);
		return page;
	}

	async Task OpenAsync(IPage page, Guid? stream)
	{
		await page.GotoAsync(ComposeSeedingUrl(stream));
		if (stream is not null)
		{
			await WaitReadyAsync(page);
		}
		else
		{
			await Expect(page.GetByTestId("btnCreateStream")).ToBeVisibleAsync(new() { Timeout = 60_000 });
		}
	}

	async Task WaitReadyAsync(IPage page) => await Expect(page.GetByTestId("ready")).ToBeVisibleAsync(new() { Timeout = 60_000 }); // a cold WASM start

	async Task ReloadAsync(IPage page)
	{
		await page.ReloadAsync();
		await WaitReadyAsync(page);
	}

	async Task<Guid> CreateStreamHereAsync(IPage page)
	{
		await page.GetByTestId("btnCreateStream").ClickAsync();
		await page.WaitForURLAsync(new Regex(@"seeding\?stream="));
		await WaitReadyAsync(page);
		return Guid.Parse(await page.GetByTestId("stream").InnerTextAsync());
	}

	async Task ExpectOnlineAsync(IPage page) => await Expect(page.GetByTestId("online")).ToHaveTextAsync("online", new() { Timeout = 30_000 });

	async Task ExpectWelcomesAsync(IPage page, int welcomes) => await Expect(page.GetByTestId("welcome-count")).ToHaveTextAsync(welcomes.ToString(), new() { Timeout = 30_000 });

	/// <summary>The counts hold for the quiet period: a seed that was going to appear late would have.</summary>
	async Task HoldsAsync(IPage page, int welcomes, int? items = null)
	{
		var deadline = DateTime.UtcNow + _quietPeriod;
		do
		{
			Assert.That(await page.GetByTestId("welcome-count").InnerTextAsync(), Is.EqualTo(welcomes.ToString()), "Welcome items in the browser");
			if (items is int i)
			{
				Assert.That(await page.GetByTestId("item-count").InnerTextAsync(), Is.EqualTo(i.ToString()), "items in the browser");
			}
			await Task.Delay(250);
		}
		while (DateTime.UtcNow < deadline);
	}

	/// <summary>The replication socket opens but the server never answers: no HELLO, so the browser never comes online.</summary>
	async Task SilenceServerAsync(IPage page) => await page.RouteWebSocketAsync(_replicationSocket, _ => { });

	async Task WipeBrowserStorageAsync(IPage page)
		=> await page.EvaluateAsync("async () => { for (const db of await indexedDB.databases()) { await new Promise(r => { const q = indexedDB.deleteDatabase(db.name); q.onsuccess = q.onerror = q.onblocked = () => r(); }); } }");

	List<IWebSocketFrame> RecordSentFrames(IPage page)
	{
		var sent = new List<IWebSocketFrame>();
		page.WebSocket += (_, ws) =>
		{
			if (_replicationSocket.IsMatch(ws.Url))
			{
				ws.FrameSent += (_, frame) =>
				{
					lock (sent)
					{
						sent.Add(frame);
					}
				};
			}
		};
		return sent;
	}

	sealed record StreamCreated(Guid StreamId);

	sealed record StreamLog(int Events, int Welcomes);
}
