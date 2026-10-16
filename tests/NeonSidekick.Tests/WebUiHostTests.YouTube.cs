using System.Net;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The YouTube tab at the host (2026-10-16): its page alone with YouTube's player let in (the path-limited policy pinned), its stream (hello
/// first, per page, never among the Chat streams, ended when the tab goes off or the player ends it), its reports (the owner's, checked and
/// limited), the tab's rules, and the audit lines. Over a real <see cref="WebVideoPlayer"/> in the fake backend.
/// </summary>
public sealed partial class WebUiHostTests
{
    private static string PageId(char c) => new(c, 22);

    private void YouTubeOn()
    {
        _backend.VideoPlayer ??= new WebVideoPlayer();
        _backend.TabsOn = new WebTabs(Chat: true, Home: true, Shopping: true, YouTube: true);
    }

    private string Events(string page, string load, bool claim = false) =>
        WebUiHost.YouTubeEventsPath + "?page=" + page + "&load=" + load + (claim ? "&claim=1" : "");

    // A stream's events, read one at a time.
    private sealed class EventReader(StreamReader reader)
    {
        public async Task<(string Name, string Data)?> NextAsync(TimeSpan? wait = null)
        {
            string? name = null;
            string data = "";
            while (true)
            {
                string? line = await reader.ReadLineAsync().WaitAsync(wait ?? TimeSpan.FromSeconds(20));
                if (line is null)
                {
                    return null;
                }

                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line[6..];
                }
                else if (line.Length == 0 && name is not null)
                {
                    return (name, data);
                }
            }
        }
    }

    private async Task<(HttpResponseMessage Response, EventReader Reader)> OpenYouTubeAsync(string page, string load, bool claim = false)
    {
        var response = await _client.SendAsync(Get(Events(page, load, claim)), HttpCompletionOption.ResponseHeadersRead);
        return (response, new EventReader(new StreamReader(await response.Content.ReadAsStreamAsync())));
    }

    private string Report(string page, string message) =>
        JsonSerializer.Serialize(new WebVideoReport { Page = page, Message = message }, WebUiJsonContext.Default.WebVideoReport);

    [Fact]
    public async Task TheYouTubePage_AloneLetsYouTubesPlayerIn_ThePathLimitedPolicyPinned()
    {
        YouTubeOn();

        using var page = await _client.SendAsync(Get("/youtube"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("youtube.js", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(
            "default-src 'self'; script-src 'self' https://www.youtube.com/iframe_api https://www.youtube.com/s/player/; style-src 'self'; img-src 'self' data: https://i.ytimg.com; frame-src https://www.youtube.com; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
            Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(page.Headers.GetValues("Referrer-Policy")));

        // Every other response keeps the pinned headers, the YouTube tab's own script and API included.
        foreach (string path in new[] { "/", "/youtube.js", "/youtube-core.js", "/api/pulse" })
        {
            using var other = await _client.SendAsync(Get(path));
            Assert.Equal(WebUiHost.ContentSecurityPolicy, Assert.Single(other.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("no-referrer", Assert.Single(other.Headers.GetValues("Referrer-Policy")));
        }

        _backend.TabsOn = new WebTabs(true, true, true);
        using var off = await _client.SendAsync(Get("/youtube.html"));
        Assert.Equal(HttpStatusCode.SeeOther, off.StatusCode);
        Assert.Equal("/", off.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task TheSharedPlayer_IsServedAsWritten_WithoutTheCookie()
    {
        using var core = await _client.SendAsync(Get("/youtube-core.js", signedIn: false));
        Assert.Equal(HttpStatusCode.OK, core.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", core.Content.Headers.ContentType?.ToString());
        Assert.Equal(File.ReadAllText(Path.Combine(RepoRoot(), "assets", "youtube", "player-core.js")).ReplaceLineEndings("\n"), (await core.Content.ReadAsStringAsync()).ReplaceLineEndings("\n"));
        Assert.True(WebUiAssets.IsStatic("youtube.js"));
        Assert.False(WebUiAssets.IsStatic(WebUiAssets.YouTubePage));
    }

    [Fact]
    public async Task TheStream_WritesHelloFirst_IsNoChatStream_AndEndsWhenTheTabGoesOff()
    {
        YouTubeOn();
        var (response, reader) = await OpenYouTubeAsync(PageId('a'), PageId('1'));
        using (response)
        {
            var hello = await reader.NextAsync();
            Assert.Equal("hello", hello?.Name);
            var h = JsonSerializer.Deserialize(hello!.Value.Data, WebUiJsonContext.Default.WebVideoHello)!;
            Assert.Equal(PageId('a'), h.Page);
            Assert.True(h.Owner);
            Assert.Equal(WebUiText.VideoWords, h.Words);
            Assert.Equal(0, _host.OpenStreams);
            Assert.Equal(1, _backend.VideoPlayer!.Streams);

            _backend.VideoPlayer.Play(new VideoRequest("aqz-KE-bpKQ"));
            Assert.Equal("load", (await reader.NextAsync())?.Name);

            _backend.TabsOn = new WebTabs(true, true, true);
            _backend.VideoPlayer.Send(VideoCommand.Pause);   // the next batch is never written: the stream ends first
            Assert.Null(await reader.NextAsync());
        }

        SpinWait.SpinUntil(() => _backend.VideoPlayer!.Streams == 0, TimeSpan.FromSeconds(10));
        Assert.Equal(0, _backend.VideoPlayer!.Streams);   // the attachment disposed as it ended
    }

    [Fact]
    public async Task TheStream_EndsWhenThePlayerEndsIt_AReconnectReplacingIt()
    {
        YouTubeOn();
        var (first, firstReader) = await OpenYouTubeAsync(PageId('a'), PageId('1'));
        using (first)
        {
            Assert.Equal("hello", (await firstReader.NextAsync())?.Name);
            var (second, secondReader) = await OpenYouTubeAsync(PageId('a'), PageId('1'));
            using (second)
            {
                Assert.True(JsonSerializer.Deserialize((await secondReader.NextAsync())!.Value.Data, WebUiJsonContext.Default.WebVideoHello)!.Owner);
                Assert.Null(await firstReader.NextAsync());   // the replaced one ends
            }
        }
    }

    [Fact]
    public async Task AReload_IsTheSameOwner_ItsVideoClosedWithItsPlace_AndADuplicateGetsAFreshId()
    {
        YouTubeOn();
        var player = _backend.VideoPlayer!;
        var (first, firstReader) = await OpenYouTubeAsync(PageId('a'), PageId('1'));
        await firstReader.NextAsync();
        player.Play(new VideoRequest("aqz-KE-bpKQ"));
        await firstReader.NextAsync();
        using (var playing = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, Report(PageId('a'), "{\"ev\":\"state\",\"id\":\"aqz-KE-bpKQ\",\"state\":1,\"position\":100,\"duration\":635}"))))
        {
            Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
        }

        var (duplicate, duplicateReader) = await OpenYouTubeAsync(PageId('a'), PageId('2'));   // the first still open: a duplicated tab
        var dup = JsonSerializer.Deserialize((await duplicateReader.NextAsync())!.Value.Data, WebUiJsonContext.Default.WebVideoHello)!;
        Assert.NotEqual(PageId('a'), dup.Page);
        Assert.False(dup.Owner);
        duplicate.Dispose();

        first.Dispose();   // the reload: its old stream gone
        SpinWait.SpinUntil(() => player.Streams == 0, TimeSpan.FromSeconds(10));
        var (reloaded, reloadedReader) = await OpenYouTubeAsync(PageId('a'), PageId('3'));
        using (reloaded)
        {
            Assert.True(JsonSerializer.Deserialize((await reloadedReader.NextAsync())!.Value.Data, WebUiJsonContext.Default.WebVideoHello)!.Owner);
            using var page = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, Report(PageId('a'), "{\"ev\":\"page\"}")));
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var resume = await reloadedReader.NextAsync();
            Assert.Equal("resume", resume?.Name);
            Assert.Equal(97, JsonSerializer.Deserialize(resume!.Value.Data, WebUiJsonContext.Default.WebVideoResume)!.Start);
            Assert.Null(player.Snapshot);
        }
    }

    [Fact]
    public async Task AReport_FromANonOwner_Is409_ABadOne400_AndTheStateIsPlacedOnTheTab()
    {
        YouTubeOn();
        var player = _backend.VideoPlayer!;
        var (owner, ownerReader) = await OpenYouTubeAsync(PageId('a'), PageId('1'));
        using (owner)
        {
            await ownerReader.NextAsync();
            player.Play(new VideoRequest("aqz-KE-bpKQ"));

            using var stranger = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, Report(PageId('b'), "{\"ev\":\"state\",\"state\":1}")));
            Assert.Equal(HttpStatusCode.Conflict, stranger.StatusCode);
            using var badId = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, Report("short", "{\"ev\":\"page\"}")));
            Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
            using var badBody = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, "{not json"));
            Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
            using var tooLong = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, Report(PageId('a'), new string(' ', WebVideoPlayer.MaxReportChars + 1))));
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
            using var forged = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, Report(PageId('a'), "{\"ev\":\"page\"}"), csrf: false));
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

            using var ok = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, Report(PageId('a'), "{\"ev\":\"state\",\"id\":\"aqz-KE-bpKQ\",\"state\":1,\"position\":3,\"duration\":635}")));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal((VideoState.Playing, VideoPlace.WebTab), (player.Snapshot!.State, player.Snapshot.Place));
            Assert.True(player.Snapshot.Version > 0);
        }
    }

    [Fact]
    public async Task ABadPageId_OrLoad_OnTheStream_Is400()
    {
        YouTubeOn();
        using var shortPage = await _client.SendAsync(Get(Events("abc", PageId('1'))));
        using var badLoad = await _client.SendAsync(Get(Events(PageId('a'), "has spaces in it!!")));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (shortPage.StatusCode, badLoad.StatusCode));
        Assert.Equal(0, _backend.VideoPlayer!.Streams);
    }

    [Fact]
    public async Task WithNoPlayer_TheYouTubeApis_AreTheTabOff404_WhateverTheTabsSay()
    {
        _backend.VideoPlayer = null;
        _backend.TabsOn = new WebTabs(true, true, true, YouTube: true);

        using var stream = await _client.SendAsync(Get(Events(PageId('a'), PageId('1'))));
        using var report = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, Report(PageId('a'), "{\"ev\":\"page\"}")));
        using var page = await _client.SendAsync(Get("/youtube"));

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (stream.StatusCode, report.StatusCode));
        Assert.Contains("\"tabOff\":true", await stream.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(WebUiText.YouTubeTabOff, JsonSerializer.Deserialize(await report.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Message);
        Assert.Equal(HttpStatusCode.SeeOther, page.StatusCode);
    }

    [Fact]
    public async Task APlayHere_AndAResume_AreAudited_WithTheAddress()
    {
        YouTubeOn();
        var messages = new List<string>();
        void Record(DiagnosticEvent e) { lock (messages) { messages.Add(e.Message); } }
        DiagnosticLog.Emitted += Record;
        try
        {
            var (a, aReader) = await OpenYouTubeAsync(PageId('a'), PageId('1'));
            await aReader.NextAsync();
            var (b, bReader) = await OpenYouTubeAsync(PageId('b'), PageId('2'), claim: true);
            await bReader.NextAsync();
            using var resumed = await _client.SendAsync(Post(WebUiHost.YouTubeReportPath, Report(PageId('b'), "{\"ev\":\"resumed\"}")));
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            a.Dispose();
            b.Dispose();
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
        }

        lock (messages)
        {
            Assert.Contains(WebUiText.YouTubeOwnerLogLine(IPAddress.Loopback), messages);
            Assert.Contains(WebUiText.YouTubeClaimLogLine(IPAddress.Loopback), messages);
            Assert.Contains(WebUiText.YouTubeResumeLogLine(IPAddress.Loopback), messages);
        }
    }

    [Fact]
    public void TheYouTubeTab_HasItsOwnArms_InTheTabsAndTheirSentences()
    {
        Assert.True(new WebTabs(false, false, false, YouTube: true).Has(WebTab.YouTube));
        Assert.False(new WebTabs(false, false, Shopping: true, YouTube: false).Has(WebTab.YouTube));   // never Shopping's
        Assert.False(new WebTabs(false, false, Shopping: false, YouTube: true).Has(WebTab.Shopping));
        Assert.False(WebTabs.All.YouTube);
        Assert.False(new WebTabs(true, true, true, true).Has((WebTab)99));
        Assert.Equal(WebUiText.YouTubeTabOff, WebUiText.TabOff(WebTab.YouTube));
        Assert.Contains("Web UI YouTube controls", WebUiText.YouTubeTabOff, StringComparison.Ordinal);
        Assert.Equal("", WebUiText.TabOff((WebTab)99));
        Assert.Equal("/youtube", new WebTabs(false, false, false, YouTube: true).FirstOtherPage);
        Assert.Equal("/shopping", new WebTabs(false, false, true, YouTube: true).FirstOtherPage);
        Assert.Equal(WebTab.YouTube, WebUiHost.TabOf(WebUiHost.YouTubeEventsPath));
        Assert.Equal(WebTab.YouTube, WebUiHost.TabOf(WebUiHost.YouTubeReportPath));
        Assert.Null(WebUiHost.TabOf("/api/youtubex"));
    }

    [Fact]
    public async Task WithOnlyYouTubeOn_SlashGoesToTheYouTubeTab()
    {
        YouTubeOn();
        _backend.TabsOn = new WebTabs(Chat: false, Home: false, Shopping: false, YouTube: true);

        var slash = await PageAsync("/");

        Assert.Equal((HttpStatusCode.SeeOther, "/youtube"), (slash.Status, slash.Location));
    }

    [Fact]
    public void TheReportBucket_IsItsOwn_InEverySwitch()
    {
        Assert.Equal((30.0, 10.0), WebRateLimit.Rate(WebRateBucket.Report));
        Assert.Equal((5.0, 5.0 / 60), WebRateLimit.Rate(WebRateBucket.SignIn));
        Assert.Contains("player reports", WebUiText.RateLimitedLogLine(IPAddress.Loopback, WebRateBucket.Report), StringComparison.Ordinal);
        Assert.Contains("sign-in attempts", WebUiText.RateLimitedLogLine(IPAddress.Loopback, WebRateBucket.SignIn), StringComparison.Ordinal);
        Assert.Equal(WebRateBucket.Report, WebRateLimit.BucketOf(WebUiHost.YouTubeReportPath, post: true));
        Assert.Null(WebRateLimit.BucketOf(WebUiHost.YouTubeEventsPath, post: false));

        // A page reporting once a second for a minute (a tick, and a command's answers now and then) is never refused.
        var time = new Fakes.ManualTimeProvider();
        var limits = new WebRateLimit(time);
        for (int second = 0; second < 60; second++)
        {
            Assert.True(limits.TryTake(IPAddress.Loopback, WebRateBucket.Report, out _));
            if (second % 10 == 0)
            {
                Assert.True(limits.TryTake(IPAddress.Loopback, WebRateBucket.Report, out _));
                Assert.True(limits.TryTake(IPAddress.Loopback, WebRateBucket.Report, out _));
            }

            time.Advance(TimeSpan.FromSeconds(1));
        }
    }
}
