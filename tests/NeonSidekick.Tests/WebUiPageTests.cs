using System.Net;
using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's pages in the machine's own browser (2026-10-15, the plan's E4): a real <see cref="WebUiHost"/> on loopback over the fake backend,
/// the pages' scripts run by headless Edge — the status row and its narrow-screen order, the transcript's folds, the store picker, the Home tab's
/// tiles and asks, the Chat badge and the theme's re-link, the pasted sign-in. Every wait polls with a time limit. No browser installed: each test
/// returns, as the PDF tests do.
/// </summary>
public sealed partial class WebUiPageTests : IAsyncLifetime
{
    private readonly FakeWebUiBackend _backend = new();
    private readonly string _token = WebUiAuth.NewToken();
    private WebUiHost _host = null!;
    private HeadlessPage? _page;

    private string Root => "http://127.0.0.1:" + _host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";

    public async Task InitializeAsync()
    {
        _host = new WebUiHost(_backend, Path.GetTempPath());
        await _host.StartAsync(new WebUiBinding(WebUiAudience.Local, 0, _token, IPAddress.Loopback), CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            _page = await HeadlessPage.StartAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_page is not null)
        {
            await _page.DisposeAsync();
        }

        await _host.DisposeAsync();
    }

    private async Task<HeadlessPage?> SignedInAsync(string path, int width = 390)
    {
        if (_page is null)
        {
            return null;   // no browser installed: nothing to prove here
        }

        await _page.ViewportAsync(width);
        await _page.SetCookieAsync(Root, WebUiAuth.CookieName(_host.Port), _token);
        await _page.GoAsync(Root + path.TrimStart('/'));
        return _page;
    }

    private static WebState State(bool busy = false, string theme = "") => new("p", "", "", "m", busy, false, false, "", [], null, "1", "tag", theme);

    // ── Chat ────────────────────────────────────────────────────────────────

    [WindowsFact]
    public async Task Chat_TheStatusRow_ShowsTheLabelItsClockAndTheUsage_AndANarrowPhoneDropsTheTokens()
    {
        _backend.Snap = new WebSnapshot([], [], State(busy: true), WebUiText.Words, null, new WebStatus("pondering", 12_000, null, new WebUsage("4.6k / 151.4k", "3%", "42 tok/s")));
        if (await SignedInAsync("/", width: 480) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('#status .label').textContent === 'pondering'", "the status row's label");
        await page.WaitAsync("/^00:1[2-9]$/.test(document.querySelector('#status .clock').textContent)", "the clock running on from 00:12");
        Assert.Equal("4.6k / 151.4k · 3% · 42 tok/s", await page.TextAsync("document.getElementById('usage').textContent"));

        await page.ViewportAsync(320);
        await page.WaitAsync("document.getElementById('usage').textContent === '3%'", "the tokens and the speed dropped at 320 px");
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('#loading').length")).GetInt32());   // no Loading banner any more

        // A busy PC with no label still spins ("working", the fallback); the turn's end clears both. The usage card opens on a tap.
        _backend.Events.Publish(new WebEvent("status", () => JsonSerializer.Serialize(new WebStatus(null, null, null, new WebUsage("4.6k", null, null)), WebUiJsonContext.Default.WebStatus)));
        await page.WaitAsync("document.querySelector('#status .label').textContent === " + JsonSerializer.Serialize(WebUiText.Working), "the fallback while the state says busy");
        _backend.Events.Publish(new WebEvent("state", () => JsonSerializer.Serialize(State(busy: false), WebUiJsonContext.Default.WebState)));
        await page.WaitAsync("document.querySelector('#status .spin').classList.contains('hidden')", "the spinner gone with its label");
        await page.EvalAsync("document.getElementById('usage').click()");
        await page.WaitAsync("!document.getElementById('usage-card').classList.contains('hidden')", "the usage card");
        Assert.Contains("1.0k / 8.0k", await page.TextAsync("document.getElementById('usage-context').textContent"), StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Chat_ThinkingFolds_ADeferredOneIsReadOnOpening_ARunFoldsAndOpens_AndTheRestDraws()
    {
        _backend.Entries[1] = new WebEntry(1, "thinking", "The whole thought.", null, null, false) { Summary = "💭 thought for 1.0s", Ended = true };
        var diff = new WebDiff("Added 1 line, removed 1 line", [new WebDiffRow("-", 2, "two"), new WebDiffRow("+", 2, "TWO")], null, 0, "");
        _backend.Snap = new WebSnapshot(
        [
            new WebEntry(1, "thinking", "", null, null, false) { Summary = "💭 thought for 1.0s", Ended = true, Deferred = true },
            new WebEntry(2, "run", "🛠️ 3 tool calls — grep ×3", null, null, false) { Keep = 1, Ended = true },
            new WebEntry(3, "tool", "🛠️ grep a", null, null, false) { Run = 2 },
            new WebEntry(4, "result", "1 match", null, null, false) { Run = 2, Joins = true },
            new WebEntry(5, "tool", "🛠️ grep b", null, null, false) { Run = 2 },
            new WebEntry(6, "tool", "🛠️ edited a.txt", null, null, false) { Diff = diff },
            new WebEntry(7, "output", "line one\nline two", null, null, false) { Ended = true },
            new WebEntry(8, "assistant", "", "🦊", null, false) { NoReply = true },
        ], [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('.entry.thinking summary') !== null", "the folded thinking");
        Assert.Equal("💭 thought for 1.0s", await page.TextAsync("document.querySelector('.entry.thinking summary').textContent"));
        await page.EvalAsync("document.querySelector('.entry.thinking details').open = true");
        await page.WaitAsync("document.querySelector('.entry.thinking .think-body').textContent === 'The whole thought.'", "the deferred text read on opening");

        // The run: two units past a keep of one, ended: folded under its head, which a tap opens.
        Assert.False((await page.EvalAsync("document.querySelector('.run-head').classList.contains('hidden')")).GetBoolean());
        Assert.Equal(2, (await page.EvalAsync("document.querySelectorAll('.run .unit.hidden').length")).GetInt32());
        await page.EvalAsync("document.querySelector('.run-head').click()");
        await page.WaitAsync("document.querySelectorAll('.run .unit.hidden').length === 0", "the run opened");
        Assert.Equal(2, (await page.EvalAsync("document.querySelector('.run .unit').children.length")).GetInt32());   // a call and its result, one unit

        Assert.Equal(2, (await page.EvalAsync("document.querySelectorAll('.diff-row').length")).GetInt32());
        Assert.True((await page.EvalAsync("document.querySelector('.diff-row').classList.contains('del')")).GetBoolean());
        Assert.Equal("line one\nline two", await page.TextAsync("document.querySelector('pre.entry.output').textContent"));
        Assert.Equal(WebUiText.Words.NoReply, await page.TextAsync("document.querySelector('.body.noreply').textContent"));

        // A deferred entry the transcript let go says so.
        _backend.Entries.Clear();
        _backend.Snap = new WebSnapshot([new WebEntry(9, "thinking", "", null, null, false) { Summary = "s", Ended = true, Deferred = true }], [], State(), WebUiText.Words);
        await page.GoAsync(Root);
        await page.WaitAsync("document.querySelector('.entry.thinking details') !== null", "the second page's thinking");
        await page.EvalAsync("document.querySelector('.entry.thinking details').open = true");
        await page.WaitAsync("document.querySelector('.entry.thinking .gone') !== null", "the gone line");
    }

    // ── Shopping ────────────────────────────────────────────────────────────

    [WindowsFact]
    public async Task Shopping_ThePicker_ShowsWithTwoStores_PostsAChange_AndARefusalPutsTheOldOneBack()
    {
        _backend.List = new WebShopping("Shopping list at Costco: 0 items to get.", null, [], "Ticked", [], "(guess)", "tag")
        {
            Stores = [new WebShopStore(1, "Aldi"), new WebShopStore(2, "Costco")],
            Active = 2,
            StoreLabel = "Store",
        };
        if (await SignedInAsync("/shopping") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('store').classList.contains('hidden') && document.querySelector('#store select').value === '2'", "the picker on Costco");
        await page.EvalAsync("(() => { const s = document.querySelector('#store select'); s.value = '1'; s.dispatchEvent(new Event('change')); })()");
        await page.WaitAsync("document.querySelector('#store select').disabled === false", "the post answered");
        Assert.Equal(1, Assert.Single(_backend.Stores).Id);

        _backend.Reply = new WebReply(WebOutcome.NotFound, WebUiText.NoSuchStore);
        await page.EvalAsync("(() => { const s = document.querySelector('#store select'); s.value = '1'; s.dispatchEvent(new Event('change')); })()");
        await page.WaitAsync("document.querySelector('#store select').value === '2'", "the old store put back");
        Assert.Contains(WebUiText.NoSuchStore, await page.TextAsync("document.getElementById('message').textContent"), StringComparison.Ordinal);
    }

    // ── Home ────────────────────────────────────────────────────────────────

    private sealed class Home : IWebHome
    {
        public bool Controls { get; set; } = true;

        public List<string> Taps { get; } = [];

        public WebHome Read() => new("live", null, Controls,
            [
                new WebHomeTile("entity:light.desk", "💡", "Desk", "", "", Controls ? "call" : "none", null, "on", "#ffcc00", 0, true),
                new WebHomeTile("entity:lock.front", "🔒", "Front Door", "", "", Controls ? "confirm" : "none", null, "off", null, 1, true),
            ],
            [], null, new WebHomePc(12, 50, null, null, null, null), new WebHomeModel("m", "host", 8000, false, false, null), new WebPulse(false, 0, null, "", "tag"), WebUiText.HomeWords);

        public WebHomeSheet? Sheet(string reference) =>
            new(reference, "Desk", "off", [new WebHomeSheetRow("light.desk", "💡", "Desk", "off", [new WebHomeAction(0, "Toggle", false)])]);

        public WebReply Tap(WebHomeTapRequest request, IPAddress? remote)
        {
            Taps.Add(request.Ref + (request.Confirmed ? " confirmed" : ""));
            return request.Ref == "entity:lock.front" && !request.Confirmed ? new WebReply(WebOutcome.Conflict, null, Prompt: "Unlock Front Door?") : WebReply.Ok;
        }

        public WebReply Act(WebHomeActRequest request, IPAddress? remote) => WebReply.Ok;

        public WebReply Scene(WebHomeSceneRequest request, IPAddress? remote) => WebReply.Ok;

        public Task<WebReply> SayAsync(WebHomeSayRequest request, IPAddress? remote, CancellationToken cancellationToken) => Task.FromResult(WebReply.Ok);

        public WebReply Video(WebHomeVideoRequest request, IPAddress? remote) => WebReply.Ok;
    }

    [WindowsFact]
    public async Task Home_TilesByTone_ATapPosts_AnUnlockAsksInASheet_AndBackClosesIt()
    {
        var home = new Home();
        _backend.Home = home;
        if (await SignedInAsync("/home") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.tile').length === 2", "the two tiles");
        Assert.True((await page.EvalAsync("document.querySelector('.tile').classList.contains('tone-on')")).GetBoolean());
        Assert.Equal(2, (await page.EvalAsync("document.querySelectorAll('.tile-group').length")).GetInt32());   // the divider's two groups
        await page.EvalAsync("document.querySelector('.tile').click()");
        await WaitForAsync(() => home.Taps.Contains("entity:light.desk"));

        await page.EvalAsync("document.querySelectorAll('.tile')[1].click()");
        await page.WaitAsync("!document.getElementById('sheet-back').classList.contains('hidden') && document.getElementById('sheet-title').textContent === 'Unlock Front Door?'", "the confirm sheet");
        await page.EvalAsync("document.querySelector('#sheet .choices .primary').click()");
        await WaitForAsync(() => home.Taps.Contains("entity:lock.front confirmed"));

        await page.EvalAsync("document.querySelector('.tile .more').click()");
        await page.WaitAsync("document.getElementById('sheet-title').textContent.startsWith('Desk')", "the desk's sheet");
        await page.EvalAsync("history.back()");
        await page.WaitAsync("document.getElementById('sheet-back').classList.contains('hidden')", "Back closing the sheet");
        Assert.Equal(2, (await page.EvalAsync("document.querySelectorAll('.meter').length")).GetInt32());   // CPU and RAM; the GPU's unread
    }

    // ── Common ──────────────────────────────────────────────────────────────

    [WindowsFact]
    public async Task TheBadge_TheTitleAndTheThemesRelink_FollowThePulse()
    {
        if (await SignedInAsync("/shopping") is not { } page)
        {
            return;
        }

        string href = await page.TextAsync("document.getElementById('theme-css').getAttribute('href')");
        Assert.Equal("/theme.css", href);
        _backend.PulseNow = new WebPulse(true, 2, null, "", "tag");
        await page.WaitAsync("document.getElementById('chat-link').dataset.badge === '2' && document.title.startsWith('(2) ')", "the badge and the title");
        _backend.PulseNow = new WebPulse(true, 0, null, "synthwave", "tag");
        await page.WaitAsync("document.getElementById('theme-css').getAttribute('href') === '/theme.css?v=synthwave'", "the theme's sheet re-linked");
        Assert.True((await page.EvalAsync("document.getElementById('chat-link').classList.contains('busy')")).GetBoolean());
    }

    [WindowsFact]
    public async Task TheSignInPage_TakesAPastedLink_AndReloadsSignedIn()
    {
        if (_page is null)
        {
            return;
        }

        await _page.ViewportAsync(390);
        await _page.GoAsync(Root + "home");
        await _page.WaitAsync("document.getElementById('signin-link') !== null", "the sign-in form");
        await _page.EvalAsync("document.getElementById('signin-link').value = " + JsonSerializer.Serialize(WebUiAuth.SignInLink("127.0.0.1", _host.Port, _token))
            + "; document.getElementById('signin-form').requestSubmit()");
        _backend.Home = new Home();
        await _page.WaitAsync("document.getElementById('tiles') !== null", "/home again, signed in", 15);
        Assert.Single(_backend.SignIns);
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "waited 10 s on the backend");
            await Task.Delay(50);
        }
    }
}
