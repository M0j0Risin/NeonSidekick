using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The Mac video window's pure parts (2026-10-07, YouTube playback on macOS): <see cref="WebKitPage"/>'s bridge for the page Windows
/// plays unchanged, the rule for whose script messages count, the data store's identifier per home, the window's Mac keys and words,
/// and the smoke's <c>video:webkit</c> where it can run. The WebKit layer itself is the smoke's on the published exe and the live run's.
/// </summary>
public sealed partial class WebKitPageTests
{
    private const ushort KeyF = 3, KeyW = 13, KeyK = 40, Escape = 53, Tab = 48, Space = 49, Left = 123, F11 = 103;

    // ── The bridge ───────────────────────────────────────────────────────────

    /// <summary>
    /// player.html touches <c>chrome.webview</c> only through the two members the bridge gives it, so the page plays on a Mac as it is.
    /// A new use in the page fails here before it fails silently on a Mac.
    /// </summary>
    [Fact]
    public void ThePage_UsesOnly_WhatTheBridgeGives()
    {
        var uses = WebViewUse().Matches(VideoPage.Text()).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["addEventListener", "postMessage"], uses);
        Assert.Contains("postMessage: text => handler.postMessage(String(text))", WebKitPage.Bridge, StringComparison.Ordinal);
        Assert.Contains("addEventListener: (type, listener) => { if (type === 'message') listeners.push(listener); }", WebKitPage.Bridge, StringComparison.Ordinal);
        Assert.Contains("window.webkit.messageHandlers." + WebKitPage.HandlerName, WebKitPage.Bridge, StringComparison.Ordinal);
        Assert.Contains("listener({ data })", WebKitPage.Bridge, StringComparison.Ordinal);   // e.data, as WebView2's PostWebMessageAsJson hands it
    }

    /// <summary>A command reaches the page as the object WebView2 would hand it: the message's JSON is the call's argument.</summary>
    [Fact]
    public void DeliverScript_PassesTheMessageAsItsObject()
    {
        Assert.Equal("window.__neonDeliver({\"cmd\":\"pause\"})", WebKitPage.DeliverScript(VideoMessages.Command(VideoCommand.Pause)));
        Assert.Equal(
            "window.__neonDeliver({\"cmd\":\"load\",\"id\":\"aqz-KE-bpKQ\",\"start\":30,\"autoplay\":true})",
            WebKitPage.DeliverScript(VideoMessages.Load(new VideoRequest("aqz-KE-bpKQ", 30))));
        Assert.Throws<ArgumentException>(() => WebKitPage.DeliverScript(" "));
    }

    /// <summary>A report crosses as the string it was, so the window's fold and versions are Windows' own.</summary>
    [Fact]
    public void AReport_FoldsAsOnWindows()
    {
        var (kind, snapshot) = VideoMessages.Apply(VideoSnapshot.Opening("M7lc1UVf-VE") with { Version = 4 },
            "{\"ev\":\"state\",\"why\":\"change\",\"id\":\"M7lc1UVf-VE\",\"title\":\"YouTube Developers Live\",\"author\":\"Google for Developers\",\"state\":1,\"position\":0.0144,\"duration\":1343.661,\"volume\":100,\"muted\":false}");

        Assert.Equal(VideoPageEvent.State, kind);
        Assert.Equal(VideoState.Playing, snapshot.State);
        Assert.Equal("Google for Developers", snapshot.Author);
        Assert.Equal(5, snapshot.Version);
    }

    [Theory]
    [InlineData(true, "https", "player.neonsidekick.example", true)]
    [InlineData(true, "https", "PLAYER.NeonSidekick.example", true)]
    [InlineData(false, "https", "player.neonsidekick.example", false)]   // a frame inside the page
    [InlineData(true, "https", "www.youtube.com", false)]
    [InlineData(true, "http", "player.neonsidekick.example", false)]
    [InlineData(true, "", "", false)]                                    // about:blank, no base URL
    [InlineData(true, null, null, false)]
    [InlineData(true, "https", "player.neonsidekick.example.evil.com", false)]
    public void IsOurFrame_IsThePagesOwnFrame_Only(bool main, string? protocol, string? host, bool ours) =>
        Assert.Equal(ours, WebKitPage.IsOurFrame(main, protocol, host));

    // ── The store ────────────────────────────────────────────────────────────

    [Fact]
    public void StoreId_IsAVersion8Uuid_TheSameForTheSameHome()
    {
        string home = Path.Combine(Path.GetTempPath(), "neon-home-a");
        string id = WebKitPage.StoreId(home);

        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-8[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$", id);
        Assert.Equal(id, WebKitPage.StoreId(home));
        Assert.Equal(id, WebKitPage.StoreId(home + Path.DirectorySeparatorChar));
        Assert.True(Guid.TryParse(id, out _));
    }

    [Fact]
    public void StoreId_DiffersByHome()
    {
        Assert.NotEqual(
            WebKitPage.StoreId(Path.Combine(Path.GetTempPath(), "neon-home-a")),
            WebKitPage.StoreId(Path.Combine(Path.GetTempPath(), "neon-home-b")));
        Assert.Throws<ArgumentException>(() => WebKitPage.StoreId(""));
    }

    /// <summary>
    /// Pinned, against a separate computation of the same recipe: the store a home was given never moves (a change would leave every
    /// Mac user's YouTube data behind in the old one).
    /// </summary>
    [UnixFact]
    public void StoreId_IsPinned() =>
        Assert.Equal("489A79C1-84F5-8F18-BB39-1A7641B6289F", WebKitPage.StoreId("/Users/someone/.neonsidekick"));

    // ── Keys and words ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(KeyW, MacKeys.CommandFlag, false, VideoKeyAction.Close)]
    [InlineData(KeyF, MacKeys.CommandFlag | MacKeys.ControlFlag, false, VideoKeyAction.ToggleFullScreen)]
    [InlineData(KeyW, MacKeys.CommandFlag | MacKeys.OptionFlag, false, VideoKeyAction.None)]
    [InlineData(KeyK, MacKeys.CommandFlag, false, VideoKeyAction.None)]
    [InlineData(F11, 0UL, false, VideoKeyAction.ToggleFullScreen)]
    [InlineData(Escape, 0UL, false, VideoKeyAction.Close)]
    [InlineData(Escape, 0UL, true, VideoKeyAction.LeaveFullScreen)]
    [InlineData(Escape, MacKeys.OptionFlag, false, VideoKeyAction.None)]
    [InlineData(Space, 0UL, false, VideoKeyAction.None)]   // the player's
    [InlineData(Left, 0UL, false, VideoKeyAction.None)]
    [InlineData(KeyK, 0UL, false, VideoKeyAction.None)]
    [InlineData(Tab, 0UL, false, VideoKeyAction.None)]     // the terminal's, through TerminalHandoff
    public void VideoAction_MacExtras_ThenWindowsRules(ushort key, ulong flags, bool fullScreen, VideoKeyAction expected) =>
        Assert.Equal(expected, MacKeys.VideoAction(key, flags, fullScreen));

    [Fact]
    public void TheMacWords_AreTheirOwn_AndWindowsKeepsItsOwn()
    {
        Assert.Equal("The video window needs a desktop session and macOS 14 or later; open_url can open the video in the browser instead.", VideoText.UnavailableMac);
        Assert.Equal("The video window's web page stopped (its WebKit process ended); play the video again to reopen the window.", VideoText.ContentEnded);
        Assert.Equal("There is no video window here (it needs a desktop session and macOS 14 or later); /youtube <words> still searches.", YouTubeText.NoWindowMac);
        Assert.Equal("There is no video window here (it needs Windows); /youtube <words> still searches.", YouTubeText.NoWindow);
        Assert.Equal("The video window needs Windows; open_url can open the video in the browser instead.", VideoText.Unavailable);
    }

    // ── The smoke's probe ────────────────────────────────────────────────────

    /// <summary>
    /// Under xUnit there is no AppKit host (the main thread is the testhost's), so the probe skips and passes; the published exe's
    /// <c>video:webkit</c> runs it for real. Off a Mac it skips by name.
    /// </summary>
    [Fact]
    public void Probe_SkipsWithoutTheHost_AndIsNamed()
    {
        var check = SmokeChecks.ProbeVideoWebKit();

        Assert.Equal("video:webkit", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("skipped: ", check.Detail, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"chrome\.webview\.(\w+)")]
    private static partial Regex WebViewUse();
}
