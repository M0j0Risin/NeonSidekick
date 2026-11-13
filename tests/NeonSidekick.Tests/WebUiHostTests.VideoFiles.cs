using System.Net;
using System.Net.Http.Headers;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A video file at the host (2026-11-12, the video-files plan): <c>/api/youtube/file/{token}/{name}</c> serves the file playing in ranges, by
/// its token alone (the name ignored), behind the sign-in, the cross-site and the tab gates; another token, a path, a closed play or the tab
/// off is a 404; over Tailscale Funnel a 403 with its sentence and an audit line until <i>Web UI video files over Funnel</i> is on.
/// </summary>
public sealed partial class WebUiHostTests
{
    private string VideoFileClip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "neon-hostfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "clip.mp4");
        File.WriteAllBytes(path, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        return path;
    }

    private HttpRequestMessage FileGet(string path, bool signedIn = true)
    {
        var request = Get(path, signedIn);
        request.Headers.Range = new RangeHeaderValue(4, 7);
        return request;
    }

    [Fact]
    public async Task AVideoFile_IsServedByItsToken_InRanges_AndNothingElseIs()
    {
        YouTubeOn();
        string clip = VideoFileClip();
        try
        {
            var request = VideoRequest.File(clip);
            _backend.VideoPlayer!.Play(request);   // no page yet: held, and served for the page that comes
            string src = WebVideoPlayer.FileAddress(request.FileToken!, request.FileName);

            using (var slice = await _client.SendAsync(FileGet(src)))
            {
                Assert.Equal(HttpStatusCode.PartialContent, slice.StatusCode);
                Assert.Equal([4, 5, 6, 7], await slice.Content.ReadAsByteArrayAsync());
                Assert.Equal("video/mp4", slice.Content.Headers.ContentType?.MediaType);
                Assert.Equal("bytes 4-7/64", slice.Content.Headers.ContentRange?.ToString());
                Assert.Equal(WebUiHost.ContentSecurityPolicy, Assert.Single(slice.Headers.GetValues("Content-Security-Policy")));
            }

            // The name is the browser's only: any other, or one whose %2F adds segments, reaches the same file.
            foreach (string name in new[] { "/other.mp4", "/a%2Fb.mp4", "" })
            {
                using var renamed = await _client.SendAsync(FileGet(WebUiHost.YouTubeFilePrefix + request.FileToken + name));
                Assert.Equal(HttpStatusCode.PartialContent, renamed.StatusCode);
            }

            foreach (string path in new[]
            {
                WebUiHost.YouTubeFilePrefix + VideoFile.NewToken() + "/clip.mp4",
                WebUiHost.YouTubeFilePrefix + "..%2F..%2Fclip.mp4",
                WebUiHost.YouTubeFilePrefix + request.FileToken!.ToUpperInvariant() + "x/clip.mp4",
            })
            {
                using var other = await _client.SendAsync(FileGet(path));
                Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
            }

            using (var signedOut = await _client.SendAsync(FileGet(src, signedIn: false)))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
            }

            var crossSite = FileGet(src);
            crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
            using (var refused = await _client.SendAsync(crossSite))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            _backend.TabsOn = new WebTabs(true, true, true);
            using (var tabOff = await _client.SendAsync(FileGet(src)))
            {
                Assert.Equal(HttpStatusCode.NotFound, tabOff.StatusCode);
            }

            YouTubeOn();
            Assert.True(_backend.VideoPlayer.Close());
            using var closed = await _client.SendAsync(FileGet(src));
            Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(clip)!, recursive: true);
        }
    }

    /// <summary>The file player is served as written, cookie-free as the YouTube core is, and the window's file page carries it at its marker.</summary>
    [Fact]
    public async Task TheFilePlayer_IsServedAsWritten_AndInTheWindowsPage()
    {
        string written = File.ReadAllText(Path.Combine(RepoRoot(), "assets", "youtube", "file-core.js")).ReplaceLineEndings("\n");
        using var script = await _client.SendAsync(Get("/" + WebUiAssets.YouTubeFile, signedIn: false));

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", script.Content.Headers.ContentType?.ToString());
        Assert.Equal(written, (await script.Content.ReadAsStringAsync()).ReplaceLineEndings("\n"));
        string page = VideoPage.FileText().ReplaceLineEndings("\n");
        Assert.Contains(written.Trim(), page, StringComparison.Ordinal);
        Assert.DoesNotContain(VideoPage.FileCoreMarker, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverFunnel_AVideoFile_IsRefused_UntilItsSettingIsOn()
    {
        YouTubeOn();
        string clip = VideoFileClip();
        var lines = new List<string>();
        void Record(DiagnosticEvent entry) { lock (lines) { lines.Add(entry.Message); } }
        DiagnosticLog.Emitted += Record;
        try
        {
            var request = VideoRequest.File(clip);
            _backend.VideoPlayer!.Play(request);
            string src = WebVideoPlayer.FileAddress(request.FileToken!, request.FileName);

            using (var refused = await _client.SendAsync(ThroughServe(FileGet(src), "203.0.113.9", funnel: true)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Equal(WebUiText.VideoFileFunnelOff, System.Text.Json.JsonSerializer.Deserialize(await refused.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)?.Message);
            }

            lock (lines) { Assert.Contains(lines, l => l.Contains("a video file was refused over Tailscale Funnel", StringComparison.Ordinal)); }

            using (var tailnet = await _client.SendAsync(ThroughServe(FileGet(src), "100.64.1.2")))
            {
                Assert.Equal(HttpStatusCode.PartialContent, tailnet.StatusCode);   // Serve on the tailnet: no Funnel
            }

            _backend.VideoFilesOverFunnel = true;
            using var allowed = await _client.SendAsync(ThroughServe(FileGet(src), "203.0.113.9", funnel: true));
            Assert.Equal(HttpStatusCode.PartialContent, allowed.StatusCode);
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
            Directory.Delete(Path.GetDirectoryName(clip)!, recursive: true);
        }
    }
}
