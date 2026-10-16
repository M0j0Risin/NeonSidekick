using System.Net;
using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The YouTube tab in the machine's own browser (2026-10-16, YouTube open links in webui-window), Edge headless and offline (never YouTube):
/// the nav's link only with the tab on (the pulse serialized as the host writes it), the page with no tab on leaving for YouTube's, the jump
/// from Chat and Shopping, the banner instead over a draft or a question, the page's own report, a player that never loads, Play here, a
/// reload's Resume, and the Chat badge. Over a real <see cref="WebVideoPlayer"/>. No browser installed: each test returns.
/// </summary>
public sealed class WebUiYouTubePageTests : IAsyncLifetime
{
    private readonly FakeWebUiBackend _backend = new() { VideoPlayer = new WebVideoPlayer(apiWaitMs: 1500) };
    private readonly string _token = WebUiAuth.NewToken();
    private WebUiHost _host = null!;
    private HeadlessPage? _page;

    private string Root => "http://127.0.0.1:" + _host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";

    private WebVideoPlayer Player => _backend.VideoPlayer!;

    public async Task InitializeAsync()
    {
        _backend.TabsOn = new WebTabs(true, true, true, YouTube: true);
        _backend.PulseNow = Pulse();
        _host = new WebUiHost(_backend, Path.GetTempPath());
        await _host.StartAsync(new WebUiBinding(WebUiAudience.Local, 0, _token, IPAddress.Loopback), CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            _page = await HeadlessPage.StartAsync(offline: true);
        }
    }

    public async Task DisposeAsync()
    {
        if (_page is not null)
        {
            await _page.DisposeAsync();
        }

        await _host.DisposeAsync();
        Player.Dispose();
    }

    private static WebPulse Pulse(bool youTube = true, string? watch = null, int asks = 0) =>
        new(false, asks, null, "", "tag", ChatTab: true, HomeTab: true, ShoppingTab: true, YouTubeTab: youTube, Watch: watch);

    private static WebState State(bool youTube = true, string? watch = null) =>
        new("p", "", "", "m", false, false, false, "", [], null, "1", "tag", "", YouTubeTab: youTube, Watch: watch);

    private async Task<HeadlessPage?> SignedInAsync(string path)
    {
        if (_page is null)
        {
            return null;
        }

        await _page.ViewportAsync(390);
        await _page.SetCookieAsync(Root, WebUiAuth.CookieName(_host.Port), _token);
        await _page.GoAsync(Root + path.TrimStart('/'));
        return _page;
    }

    private Task<bool> HiddenAsync(HeadlessPage page, string selector) =>
        page.EvalAsync("document.querySelector(" + JsonSerializer.Serialize(selector) + ").classList.contains('hidden')").ContinueWith(t => t.Result.GetBoolean(), TaskScheduler.Default);

    [WindowsFact]
    public async Task TheNavsLink_ShowsOnlyWithTheTabOn()
    {
        _backend.PulseNow = Pulse(youTube: false);
        if (await SignedInAsync("/shopping") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('header.top nav').classList.contains('ready')", "the nav shown");
        Assert.True(await HiddenAsync(page, "nav a[href=\"/youtube\"]"));

        _backend.PulseNow = Pulse(youTube: true);
        await page.WaitAsync("!document.querySelector('nav a[href=\"/youtube\"]').classList.contains('hidden')", "the YouTube link shown once the pulse says so", 15);
    }

    [WindowsFact]
    public async Task ThePageWithNoTabOn_LeavesForSlash_WhenOnlyYouTubeIsOn()
    {
        _backend.TabsOn = new WebTabs(false, false, false);
        _backend.PulseNow = new WebPulse(false, 0, null, "", "tag", false, false, false);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.getElementById('notabs') !== null", "the page that says none is on");
        Assert.Contains("YouTube open links in", await page.TextAsync("document.getElementById('notabs').textContent"), StringComparison.Ordinal);

        _backend.TabsOn = new WebTabs(false, false, false, YouTube: true);
        _backend.PulseNow = new WebPulse(false, 0, null, "", "tag", false, false, false, YouTubeTab: true);
        await page.WaitAsync("document.body.dataset.tab === 'youtube'", "the YouTube tab once it is on", 15);
    }

    [WindowsFact]
    public async Task Watch_TakesChatToTheTab_AndAPulseTakesShoppingThere()
    {
        _backend.Snap = new WebSnapshot([], [], State(watch: WebUiText.WatchBanner), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.body.dataset.tab === 'youtube'", "Chat gone to the YouTube tab");

        _backend.PulseNow = Pulse(watch: WebUiText.WatchBanner);
        await page.GoAsync(Root + "shopping");
        await page.WaitAsync("document.body.dataset.tab === 'youtube'", "Shopping gone to the YouTube tab", 15);
    }

    [WindowsFact]
    public async Task Chat_WithADraft_ShowsTheBannerAndStays()
    {
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.body.dataset.tab === 'chat' && document.querySelector('header.top nav').classList.contains('ready')", "the chat");
        await page.EvalAsync("(() => { const d = document.getElementById('draft'); d.value = 'half a thought'; d.dispatchEvent(new Event('input')); })()");
        _backend.Events.Publish(new WebEvent("watch", () => JsonSerializer.Serialize(new WebTextBack(WebUiText.WatchBanner), WebUiJsonContext.Default.WebTextBack)));
        await page.WaitAsync("document.getElementById('watch-banner') !== null", "the banner");
        Assert.Equal(WebUiText.WatchBanner, await page.TextAsync("document.getElementById('watch-banner').textContent"));
        Assert.Equal("/youtube", await page.TextAsync("new URL(document.getElementById('watch-banner').href).pathname"));
        Assert.Equal("chat", await page.TextAsync("document.body.dataset.tab"));
    }

    [WindowsFact]
    public async Task Chat_AFreshConnectWithAnAskAndWatch_DrawsTheCardAndStays_AndAnsweringItNeverJumps()
    {
        var ask = new WebAskView("a1", "yesno", "Allow?", null, null, null, [new WebChoice("y", "Yes"), new WebChoice("n", "No")], null, null, null);
        _backend.Snap = new WebSnapshot([], [ask], State(watch: WebUiText.WatchBanner), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('#asks section.ask') !== null && document.getElementById('watch-banner') !== null", "the card and the banner");
        _backend.Events.Publish(new WebEvent("asks", () => JsonSerializer.Serialize(new WebAsks([]), WebUiJsonContext.Default.WebAsks)));
        await page.WaitAsync("document.querySelector('#asks section.ask') === null", "the ask gone");
        _backend.Events.Publish(new WebEvent("state", () => JsonSerializer.Serialize(State(watch: WebUiText.WatchBanner), WebUiJsonContext.Default.WebState)));
        await Task.Delay(500);
        Assert.Equal("chat", await page.TextAsync("document.body.dataset.tab"));
        Assert.NotEqual(0, (await page.EvalAsync("document.querySelectorAll('#watch-banner').length")).GetInt32());
    }

    [WindowsFact]
    public async Task TheYouTubePage_IsThePlayer_AndWithNoNetworkItsLoadFails()
    {
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.getElementById('hint').textContent.length > 0", "the tab's words");
        SpinWait.SpinUntil(() => Player.Owner is not null, TimeSpan.FromSeconds(10));
        Assert.NotNull(Player.Owner);
        Assert.Equal(Player.Owner, await page.TextAsync("sessionStorage.getItem('neon.youtube.page')"));

        Player.Play(new VideoRequest("aqz-KE-bpKQ"));
        SpinWait.SpinUntil(() => Player.Snapshot?.State == VideoState.Failed, TimeSpan.FromSeconds(15));
        Assert.Equal(VideoState.Failed, Player.Snapshot?.State);
        Assert.Equal(WebUiText.PlayerFailed("api"), Player.Snapshot!.Failure);
        await page.WaitAsync("document.getElementById('hint').textContent === " + JsonSerializer.Serialize(WebUiText.VideoWords.Failed), "the failure's words");
    }

    [WindowsFact]
    public async Task ASecondPage_ShowsPlayHere_AndItsTapClaimsPlayback()
    {
        using var other = Player.Attach(new string('z', 22), new string('9', 22), false, null, () => { });   // another page plays
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('play-here').classList.contains('hidden')", "Play here");
        Assert.Equal(WebUiText.VideoWords.PlayHere, await page.TextAsync("document.getElementById('play-here').textContent"));
        Assert.Equal(WebUiText.VideoWords.Waiting, await page.TextAsync("document.getElementById('hint').textContent"));

        await page.EvalAsync("document.getElementById('play-here').click()");
        await page.WaitAsync("document.getElementById('play-here').classList.contains('hidden')", "the page plays now");
        Assert.Equal(await page.TextAsync("sessionStorage.getItem('neon.youtube.page')"), Player.Owner);
        var told = new List<string>();
        while (other.Events.TryRead(out var e))
        {
            told.Add(e.Name);
        }

        Assert.Contains("taken", told);
    }

    [WindowsFact]
    public async Task AReload_KeepsThePagesId_AndShowsResume_NotPlayHere()
    {
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        SpinWait.SpinUntil(() => Player.Owner is not null, TimeSpan.FromSeconds(10));
        string id = Player.Owner!;
        Player.Play(new VideoRequest("aqz-KE-bpKQ"));
        Player.Report(id, "{\"ev\":\"state\",\"id\":\"aqz-KE-bpKQ\",\"title\":\"Big Buck Bunny\",\"state\":1,\"position\":195,\"duration\":635}", null);

        // The old page's stream closed first, so the server reads the new one as the reload it is (a duplicate otherwise, promoted 5 s later).
        await page.EvalAsync("source.close()");
        SpinWait.SpinUntil(() => Player.Streams == 0, TimeSpan.FromSeconds(10));
        await page.GoAsync(Root + "youtube");
        await page.WaitAsync("!document.getElementById('resume').classList.contains('hidden')", "the Resume button", 20);
        Assert.Equal("Resume \"Big Buck Bunny\" at 3:12", await page.TextAsync("document.getElementById('resume').textContent"));
        Assert.True(await HiddenAsync(page, "#play-here"));
        Assert.Equal(id, await page.TextAsync("sessionStorage.getItem('neon.youtube.page')"));
    }

    [WindowsFact]
    public async Task TheNavsChatBadge_ShowsAWaitingQuestion()
    {
        _backend.PulseNow = Pulse(asks: 1);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.getElementById('chat-link').dataset.badge === '1'", "the badge's count");
    }
}
