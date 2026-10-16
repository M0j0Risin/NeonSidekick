using System.Net;
using System.Text.Json;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The YouTube tab at the host, later on 2026-10-16: a page saying it is in view (<c>/api/youtube/seen</c>, the stream's <c>visible=</c>, a tap's
/// own claim id), and the saved list (the tagged read, the five posts, their buckets, the tab's rules).
/// </summary>
public sealed partial class WebUiHostTests
{
    private string Seen(string page, bool visible) =>
        JsonSerializer.Serialize(new WebVideoSeen { Page = page, Visible = visible }, WebUiJsonContext.Default.WebVideoSeen);

    // ── In view ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Seen_IsOkFromAnAttachedPage_409FromAStranger_400ForABadId_AndTheTabOff404()
    {
        YouTubeOn();
        var player = _backend.VideoPlayer!;
        var (a, aReader) = await OpenYouTubeAsync(PageId('a'), PageId('1'));
        using (a)
        {
            await aReader.NextAsync();
            using var ok = await _client.SendAsync(Post(WebUiHost.YouTubeSeenPath, Seen(PageId('a'), false)));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.False(player.VisibleOf(PageId('a')));

            using var stranger = await _client.SendAsync(Post(WebUiHost.YouTubeSeenPath, Seen(PageId('b'), true)));
            Assert.Equal(HttpStatusCode.Conflict, stranger.StatusCode);
            using var bad = await _client.SendAsync(Post(WebUiHost.YouTubeSeenPath, Seen("short", true)));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            _backend.TabsOn = new WebTabs(true, true, true);
            using var off = await _client.SendAsync(Post(WebUiHost.YouTubeSeenPath, Seen(PageId('a'), true)));
            Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
            Assert.Contains("\"tabOff\":true", await off.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(WebRateBucket.Report, WebRateLimit.BucketOf(WebUiHost.YouTubeSeenPath, post: true));
        Assert.Equal(WebTab.YouTube, WebUiHost.TabOf(WebUiHost.YouTubeSeenPath));
    }

    [Fact]
    public async Task TheStreamsVisible_IsRead_NoneMeaningInView_AndATapsClaimIdIsChecked()
    {
        YouTubeOn();
        var player = _backend.VideoPlayer!;
        using var hidden = await _client.SendAsync(Get(Events(PageId('a'), PageId('1')) + "&visible=0"), HttpCompletionOption.ResponseHeadersRead);
        SpinWait.SpinUntil(() => player.Streams == 1, TimeSpan.FromSeconds(10));
        Assert.False(player.VisibleOf(PageId('a')));

        using var plain = await _client.SendAsync(Get(Events(PageId('b'), PageId('2'))), HttpCompletionOption.ResponseHeadersRead);
        SpinWait.SpinUntil(() => player.Streams == 2, TimeSpan.FromSeconds(10));
        Assert.True(player.VisibleOf(PageId('b')));
        Assert.Equal(PageId('b'), player.Owner);   // in view, the owner idle: b plays

        using var badClaim = await _client.SendAsync(Get(Events(PageId('c'), PageId('3')) + "&claim=nope"));
        Assert.Equal(HttpStatusCode.BadRequest, badClaim.StatusCode);
        using var claim = await _client.SendAsync(Get(Events(PageId('a'), PageId('1')) + "&visible=0&claim=" + PageId('x')), HttpCompletionOption.ResponseHeadersRead);
        SpinWait.SpinUntil(() => player.Owner == PageId('a'), TimeSpan.FromSeconds(10));
        Assert.Equal(PageId('a'), player.Owner);
    }

    // ── The saved list ──────────────────────────────────────────────────────

    [Fact]
    public async Task TheSavedList_IsTagged_A304Unchanged_404WithNone_AndTheTabOff404()
    {
        YouTubeOn();
        lock (_backend.ListsGate)
        {
            _backend.SavedList.Add(Fakes.FakeWebUiBackend.SavedVideo("aqz-KE-bpKQ", "Big Buck Bunny"));
        }

        using var first = await _client.SendAsync(Get(WebUiHost.YouTubeSavedPath));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = JsonSerializer.Deserialize(await first.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebSavedVideos)!;
        Assert.Equal("Big Buck Bunny", Assert.Single(saved.Items).Title);
        var again = Get(WebUiHost.YouTubeSavedPath);
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

        _backend.SavedOn = false;
        using var none = await _client.SendAsync(Get(WebUiHost.YouTubeSavedPath));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        _backend.SavedOn = true;
        _backend.TabsOn = new WebTabs(true, true, true);
        using var off = await _client.SendAsync(Get(WebUiHost.YouTubeSavedPath));
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        Assert.Contains("\"tabOff\":true", await off.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSavedPosts_Answer200_404_409_And422()
    {
        YouTubeOn();
        var player = _backend.VideoPlayer!;
        lock (_backend.ListsGate)
        {
            _backend.SavedList.AddRange([Fakes.FakeWebUiBackend.SavedVideo("aqz-KE-bpKQ", "Big Buck Bunny"), Fakes.FakeWebUiBackend.SavedVideo("jNQXAC9IVRw", "Me at the zoo")]);
        }

        static string Play(string page, string id) => JsonSerializer.Serialize(new WebSavedPlay { Page = page, Id = id }, WebUiJsonContext.Default.WebSavedPlay);
        static string Id(string id) => JsonSerializer.Serialize(new WebSavedId { Id = id }, WebUiJsonContext.Default.WebSavedId);
        static string Move(string id, string to) => JsonSerializer.Serialize(new WebSavedMove { Id = id, To = to }, WebUiJsonContext.Default.WebSavedMove);

        // A play from a page with no stream: 409; with one, the play loads there.
        using (var notConnected = await _client.SendAsync(Post(WebUiHost.YouTubeSavedPlayPath, Play(PageId('a'), "aqz-KE-bpKQ"))))
        {
            Assert.Equal(HttpStatusCode.Conflict, notConnected.StatusCode);
        }

        var (a, aReader) = await OpenYouTubeAsync(PageId('a'), PageId('1'));
        using (a)
        {
            await aReader.NextAsync();
            using var played = await _client.SendAsync(Post(WebUiHost.YouTubeSavedPlayPath, Play(PageId('a'), "aqz-KE-bpKQ")));
            Assert.Equal(HttpStatusCode.OK, played.StatusCode);
            Assert.Equal("load", (await aReader.NextAsync())?.Name);
            Assert.Equal("aqz-KE-bpKQ", player.Snapshot?.VideoId);
            using var gone = await _client.SendAsync(Post(WebUiHost.YouTubeSavedPlayPath, Play(PageId('a'), "xxxxxxxxxxx")));
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        using (var moved = await _client.SendAsync(Post(WebUiHost.YouTubeSavedMovePath, Move("jNQXAC9IVRw", "top"))))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        lock (_backend.ListsGate)
        {
            Assert.Equal("jNQXAC9IVRw", _backend.SavedList[0].Id);
        }

        using (var sideways = await _client.SendAsync(Post(WebUiHost.YouTubeSavedMovePath, Move("jNQXAC9IVRw", "sideways"))))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, sideways.StatusCode);
        }

        using (var removed = await _client.SendAsync(Post(WebUiHost.YouTubeSavedRemovePath, Id("jNQXAC9IVRw"))))
        using (var removedAgain = await _client.SendAsync(Post(WebUiHost.YouTubeSavedRemovePath, Id("jNQXAC9IVRw"))))
        {
            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NotFound), (removed.StatusCode, removedAgain.StatusCode));
        }

        using (var nothingPlaying = await _client.SendAsync(Post(WebUiHost.YouTubeSavedSavePath, "{}")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, nothingPlaying.StatusCode);
        }

        _backend.SavedPlaying = new WebSavedPlaying("dQw4w9WgXcQ", "A song", false);
        using (var savedNow = await _client.SendAsync(Post(WebUiHost.YouTubeSavedSavePath, "{}")))
        {
            Assert.Equal(HttpStatusCode.OK, savedNow.StatusCode);
        }

        using (var cleared = await _client.SendAsync(Post(WebUiHost.YouTubeSavedClearPath, "{}")))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        }

        lock (_backend.ListsGate)
        {
            Assert.Empty(_backend.SavedList);
        }

        Assert.Contains("clear", _backend.SavedChanges);
        using var badBody = await _client.SendAsync(Post(WebUiHost.YouTubeSavedRemovePath, "{not json"));
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
    }

    [Fact]
    public async Task TheSavedPosts_AreTheTabs_404WhenItIsOff_AndInTheActBucket()
    {
        YouTubeOn();
        _backend.TabsOn = new WebTabs(true, true, true);
        foreach (string path in new[] { WebUiHost.YouTubeSavedPlayPath, WebUiHost.YouTubeSavedRemovePath, WebUiHost.YouTubeSavedClearPath, WebUiHost.YouTubeSavedMovePath, WebUiHost.YouTubeSavedSavePath })
        {
            using var off = await _client.SendAsync(Post(path, "{}"));
            Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
            Assert.Equal(WebRateBucket.Act, WebRateLimit.BucketOf(path, post: true));
            Assert.Equal(WebTab.YouTube, WebUiHost.TabOf(path));
        }

        Assert.Empty(_backend.SavedChanges);
        Assert.Null(WebRateLimit.BucketOf(WebUiHost.YouTubeSavedPath, post: false));
    }

    [Fact]
    public void TheThumbnailsHost_IsInTheYouTubePagesPolicyAlone()
    {
        Assert.Contains("img-src 'self' data: https://i.ytimg.com;", WebUiHost.YouTubeContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("ytimg", WebUiHost.ContentSecurityPolicy, StringComparison.Ordinal);
    }
}
