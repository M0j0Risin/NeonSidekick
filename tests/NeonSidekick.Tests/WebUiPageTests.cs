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

    /// <summary>
    /// An event published once the page's stream has connected (2026-10-19, the CI-like runs): the hub keeps nothing for a stream that comes
    /// later, and a page's stream connects after its document loads, so an event published straight after a load could be lost on a slow machine.
    /// </summary>
    private void PublishWhenStreaming(WebEvent item)
    {
        Assert.True(SpinWait.SpinUntil(() => _backend.Events.Count > 0, TimeSpan.FromSeconds(10)), "the page's event stream never connected");
        _backend.Events.Publish(item);
    }

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
        PublishWhenStreaming(new WebEvent("status", () => JsonSerializer.Serialize(new WebStatus(null, null, null, new WebUsage("4.6k", null, null)), WebUiJsonContext.Default.WebStatus)));
        await page.WaitAsync("document.querySelector('#status .label').textContent === " + JsonSerializer.Serialize(WebUiText.Working), "the fallback while the state says busy");
        PublishWhenStreaming(new WebEvent("state", () => JsonSerializer.Serialize(State(busy: false), WebUiJsonContext.Default.WebState)));
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

        /// <summary>The video card (2026-10-16), none by default.</summary>
        public WebHomeVideo? VideoCard { get; set; }

        /// <summary>The rooms (2026-10-16; the scenes' boxes until then), none by default; changed between two reads by the per-box test.</summary>
        public IReadOnlyList<WebHomeRoom> Rooms { get; set; } = [];

        /// <summary>A row's acts (2026-10-16), as <c>ref entity index label</c>, a confirmed one marked.</summary>
        public List<string> Acts { get; } = [];

        public List<string> Scenes { get; } = [];

        public WebHome Read() => new("live", null, Controls,
            [
                new WebHomeTile("entity:light.desk", "💡", "Desk", "", "", Controls ? "call" : "none", null, "on", "#ffcc00", 0, true),
                new WebHomeTile("entity:lock.front", "🔒", "Front Door", "", "", Controls ? "confirm" : "none", null, "off", null, 1, true),
            ],
            Rooms, VideoCard, new WebHomePc(12, 50, null, null, null, null), new WebHomeModel("m", "host", 8000, false, false, null), new WebPulse(false, 0, null, "", "tag"), WebUiText.HomeWords);

        public WebHomeSheet? Sheet(string reference) =>
            new(reference, "Desk", "off", [new WebHomeSheetRow("light.desk", "💡", "Desk", "off", [new WebHomeAction(0, "Toggle", false)])]);

        public WebReply Tap(WebHomeTapRequest request, IPAddress? remote)
        {
            Taps.Add(request.Ref + (request.Confirmed ? " confirmed" : ""));
            return request.Ref == "entity:lock.front" && !request.Confirmed ? new WebReply(WebOutcome.Conflict, null, Prompt: "Unlock Front Door?") : WebReply.Ok;
        }

        public WebReply Act(WebHomeActRequest request, IPAddress? remote)
        {
            Acts.Add(request.Ref + " " + request.Entity + " " + request.Index + " " + request.Label + (request.Confirmed ? " confirmed" : ""));
            return request.Entity == "lock.den_door" && !request.Confirmed ? new WebReply(WebOutcome.Conflict, null, Prompt: "Unlock Door?") : WebReply.Ok;
        }

        public WebReply Scene(WebHomeSceneRequest request, IPAddress? remote)
        {
            Scenes.Add(request.Id ?? "");
            return WebReply.Ok;
        }

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

        // Each PC meter is a row of label, bar and value (2026-10-16: the Usage card's global .meter squeezed them into 8 px pills, labels
        // clipped, and the count above stayed green): measured in the browser.
        string rows = await page.TextAsync("JSON.stringify([...document.querySelectorAll('#pc .meter')].map(r => {"
            + " const l = r.querySelector('.label'), v = r.querySelector('.value'), b = r.querySelector('.bar');"
            + " return [r.getBoundingClientRect().height, l.clientHeight, l.scrollHeight, v.clientHeight, v.scrollHeight, b.getBoundingClientRect().height, r.clientHeight, r.scrollHeight]; }))");
        foreach (var row in JsonDocument.Parse(rows).RootElement.EnumerateArray())
        {
            double[] m = [.. row.EnumerateArray().Select(x => x.GetDouble())];
            Assert.True(m[0] >= 16, "a meter row " + m[0] + " px tall, a pill");
            Assert.True(m[1] > 0 && m[2] <= m[1], "its label clipped: " + m[2] + " in " + m[1]);
            Assert.True(m[3] > 0 && m[4] <= m[3], "its value clipped: " + m[4] + " in " + m[3]);
            Assert.Equal(8, m[5], 0.5);
            Assert.True(m[7] <= m[6], "the row clips its content: " + m[7] + " in " + m[6]);
        }
    }

    /// <summary>Home's video card under YouTube open links in (2026-10-16): a video waiting for its tab draws no remote, its meta naming where it plays.</summary>
    [WindowsFact]
    public async Task Home_AWaitingVideosCard_HasNoRemote_AndItsMetaNamesWhereItPlays()
    {
        string where = WebUiText.VideoWhere(NeonSidekick.Viewer.VideoPlace.WebTab);
        _backend.Home = new Home { VideoCard = new WebHomeVideo("waiting", "aqz-KE-bpKQ", null, 0, 0, 100, false, null, null, null, where) };
        if (await SignedInAsync("/home") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('video').classList.contains('hidden')", "the video card");
        Assert.Contains(where, await page.TextAsync("document.querySelector('#video .video-meta').textContent"), StringComparison.Ordinal);
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('#video .remote').length")).GetInt32());

        _backend.Home = new Home { VideoCard = new WebHomeVideo("playing", "Big Buck Bunny", "Blender", 3, 635, 100, false, null, null, null, where) };
        await page.GoAsync(Root + "home");
        await page.WaitAsync("document.querySelectorAll('#video .remote').length === 1", "the remote for a playing video");
    }

    /// <summary>The rooms the Home tests read (2026-10-16): Den on the bar with a lamp, a lock and a scene; Bedroom with its switch and two scenes; scenes with no area.</summary>
    private static IReadOnlyList<WebHomeRoom> TestRooms(string denCount = "1 of 2 on") =>
    [
        new WebHomeRoom("den", "area:den", "Den", true, denCount, "on", null, "#ffcc00", true,
            [
                new WebHomeDevice("light.den_lamp", "💡", "Lamp", "on · 80%", "Lamp: on", "switch", true, "Toggle", false, null, "on", "#ffcc00"),
                new WebHomeDevice("lock.den_door", "🔒", "Door", "locked", "Door: locked", "button", null, "Unlock", true, null, "off", null),
                new WebHomeDevice("climate.den", "🔥", "Thermostat", "heat", null, "none", null, null, false, null, "on", null),
            ],
            [new WebHomeScene("scene.den_relax", "Den Relax", "Relax")]),
        new WebHomeRoom("bed", "area:bed", "Bedroom", false, "0 of 1 on", "off", null, null, false, [],
            [new WebHomeScene("scene.bed_candle", "Bedroom Candle", "Candle"), new WebHomeScene("scene.bed_night", "Bedroom Night", "Night")]),
        new WebHomeRoom(WebHomeView.NoAreaKey, null, "No area", false, null, "off", null, null, null, [], [new WebHomeScene("scene.party", "Party", "Party")]),
    ];

    /// <summary>
    /// The rooms (2026-10-16, the user's ask; the scenes' boxes until then): drawn in the server's order under the Rooms heading, a home bar room's
    /// box open and another's folded, a scene labeled without its room and titled with its whole name, posting its id; a fold the user toggles kept
    /// through a redraw and a reload; and a change in one room draws that box alone — another box is the same node after the read, its fold kept.
    /// </summary>
    [WindowsFact]
    public async Task Home_TheRooms_AreBoxes_TheBarsOpen_AToggledFoldIsKept_AndOneRoomsChangeRedrawsItAlone()
    {
        var home = new Home { Rooms = TestRooms() };
        _backend.Home = home;
        if (await SignedInAsync("/home") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.room-box').length === 3", "the three boxes");
        Assert.Equal(WebUiText.HomeWords.Rooms, await page.TextAsync("document.querySelector('#rooms > summary').textContent"));
        Assert.Equal("[\"den\",\"bed\",\"none\"]", await page.TextAsync("JSON.stringify([...document.querySelectorAll('.room-box')].map(g => g.dataset.key))"));
        Assert.Equal("[true,false,false]", await page.TextAsync("JSON.stringify([...document.querySelectorAll('.room-box > details')].map(g => g.open))"));
        Assert.Equal("1 of 2 on", await page.TextAsync("document.querySelector('.room-box .room-count').textContent"));
        Assert.Equal("Relax", await page.TextAsync("document.querySelector('.room-scene-row button').textContent"));
        Assert.Equal("Den Relax", await page.TextAsync("document.querySelector('.room-scene-row button').title"));
        Assert.Equal("Den Relax", await page.TextAsync("document.querySelector('.room-scene-row button').getAttribute('aria-label')"));
        Assert.Equal(WebUiText.HomeWords.Scenes, await page.TextAsync("document.querySelector('.room-scenes-label').textContent"));
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('.room-box[data-key=\"none\"] .room-switch').length")).GetInt32());   // no lights: no switch

        await page.EvalAsync("document.querySelector('.room-scene-row button').click()");
        await WaitForAsync(() => home.Scenes.Contains("scene.den_relax"));

        // A light in Den changes: Den's box is drawn again, Bedroom's node is the same object with its fold.
        await page.EvalAsync("(() => { window.__bed = document.querySelector('.room-box[data-key=\"bed\"]'); document.querySelector('.room-box[data-key=\"bed\"] > details').open = true; })()");
        await Task.Delay(100);
        home.Rooms = TestRooms("2 of 2 on");
        await page.WaitAsync("document.querySelector('.room-box .room-count').textContent === '2 of 2 on'", "Den drawn again", 10);
        Assert.True((await page.EvalAsync("document.querySelector('.room-box[data-key=\"bed\"]') === window.__bed")).GetBoolean());
        Assert.True((await page.EvalAsync("document.querySelector('.room-box[data-key=\"bed\"] > details').open")).GetBoolean());

        // Fold Den; a changed read (the controls going off) rebuilds the boxes and keeps both folds, the controls disabled.
        await page.EvalAsync("document.querySelector('.room-box[data-key=\"den\"] > details').open = false");
        await Task.Delay(100);
        home.Controls = false;
        await page.WaitAsync("document.querySelector('.room-scene-row button').disabled && document.querySelector('.room-switch').disabled", "the rebuilt boxes, disabled", 10);
        Assert.Equal("[false,true,false]", await page.TextAsync("JSON.stringify([...document.querySelectorAll('.room-box > details')].map(g => g.open))"));

        await page.GoAsync(Root + "home");
        await page.WaitAsync("document.querySelectorAll('.room-box').length === 3", "the boxes after a reload");
        Assert.Equal("[false,true,false]", await page.TextAsync("JSON.stringify([...document.querySelectorAll('.room-box > details')].map(g => g.open))"));
    }

    /// <summary>
    /// A room's controls (2026-10-16): the header's switch posts the room's tap and never folds or unfolds its box; a row's switch posts its act;
    /// a lock's button asks first, its yes posted confirmed; the › opens the device's sheet.
    /// </summary>
    [WindowsFact]
    public async Task Home_TheRooms_TheHeadersSwitch_ARowsSwitch_ALocksButtonAsks_AndTheMoreOpensTheSheet()
    {
        var home = new Home { Rooms = TestRooms() };
        _backend.Home = home;
        if (await SignedInAsync("/home") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.room-box').length === 3", "the three boxes");
        Assert.Equal("true", await page.TextAsync("document.querySelector('.room-box[data-key=\"den\"] > .room-switch').getAttribute('aria-checked')"));

        // Bedroom is folded: its header's switch taps the room, the box stays folded.
        await page.EvalAsync("document.querySelector('.room-box[data-key=\"bed\"] > .room-switch').click()");
        await WaitForAsync(() => home.Taps.Contains("area:bed"));
        Assert.False((await page.EvalAsync("document.querySelector('.room-box[data-key=\"bed\"] > details').open")).GetBoolean());

        // The All lights row's switch is the same tap.
        await page.EvalAsync("document.querySelector('.room-box[data-key=\"den\"] .room-all .room-switch').click()");
        await WaitForAsync(() => home.Taps.Count(t => t == "area:den") == 1);

        await page.EvalAsync("document.querySelector('.room-row[data-entity=\"light.den_lamp\"] .room-switch').click()");
        await WaitForAsync(() => home.Acts.Contains("entity:light.den_lamp light.den_lamp 0 Toggle"));
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('.room-row[data-entity=\"climate.den\"] .room-switch, .room-row[data-entity=\"climate.den\"] .room-act').length")).GetInt32());

        await page.EvalAsync("document.querySelector('.room-row[data-entity=\"lock.den_door\"] .room-act').click()");
        await page.WaitAsync("document.getElementById('sheet-title').textContent === 'Unlock Door?'", "the question first");
        await page.EvalAsync("document.querySelector('#sheet-body .choices button.primary').click()");
        await WaitForAsync(() => home.Acts.Contains("entity:lock.den_door lock.den_door 0 Unlock confirmed"));

        await page.WaitAsync("document.getElementById('sheet-back').classList.contains('hidden')", "the question's sheet closed");
        await page.EvalAsync("document.querySelector('.room-row[data-entity=\"light.den_lamp\"] .room-more').click()");
        await page.WaitAsync("!document.getElementById('sheet-back').classList.contains('hidden') && document.getElementById('sheet-title').textContent.startsWith('Desk')", "the device's sheet");
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

    /// <summary>Neon.post's keepalive (later on 2026-10-16: a YouTube tab saying it is hidden as it goes) reaches fetch; without it, none.</summary>
    [WindowsFact]
    public async Task Post_PassesKeepaliveToFetch()
    {
        if (await SignedInAsync("/shopping") is not { } page)
        {
            return;
        }

        await page.WaitAsync("typeof Neon !== 'undefined'", "the shared script");
        await page.EvalAsync("window.__ka = []; (() => { const f = window.fetch; window.fetch = (u, o) => { window.__ka.push(!!(o && o.keepalive)); return f(u, o); }; })()");
        await page.EvalAsync("Neon.post('/api/stop', {}, { keepalive: true })");
        await page.EvalAsync("Neon.post('/api/stop', {})");
        await page.WaitAsync("window.__ka.length >= 2", "both posts");
        Assert.Equal("[true,false]", await page.TextAsync("JSON.stringify(window.__ka.slice(0, 2))"));
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
