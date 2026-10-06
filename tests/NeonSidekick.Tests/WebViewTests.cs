using System.Runtime.CompilerServices;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The video window's WebView2 layer (2026-10-05, the YouTube plan): the page and its host (<see cref="VideoPage"/>), the
/// app-made COM handler objects driven through their own vtables as WebView2 drives them (<see cref="WebViewHandler"/>), and
/// the smoke's probe of the whole chain under the JIT (the published exe's <c>video:webview2</c> is the AOT proof).
/// </summary>
public unsafe class WebViewTests : IDisposable
{
    private static readonly Guid SomeIid = new("9ADBE429-F36D-432B-9DDC-F8881FBD76E3");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "neon-webview-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ── VideoPage ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://player.neonsidekick.example/player.html", true)]
    [InlineData("https://player.neonsidekick.example/player.html?v=aqz-KE-bpKQ&t=30", true)]
    [InlineData("https://PLAYER.NeonSidekick.example/other.html", true)]
    [InlineData("http://player.neonsidekick.example/player.html", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ", false)]
    [InlineData("https://player.neonsidekick.example.evil.com/", false)]
    [InlineData("file:///C:/player.html", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOurs_IsThePageHost_OverHttps_Only(string? uri, bool ours)
    {
        Assert.Equal(ours, VideoPage.IsOurs(uri));
    }

    [Fact]
    public void Url_AndFolders_SitUnderTheHome()
    {
        Assert.Equal("https://player.neonsidekick.example/player.html", VideoPage.Url);
        Assert.True(VideoPage.IsOurs(VideoPage.Url));
        Assert.Equal(Path.Combine("home", "webview2"), VideoPage.UserDataFolder("home"));
        Assert.Equal(Path.Combine("home", "webview2", "site"), VideoPage.SiteFolder("home"));
    }

    /// <summary>The page speaks before YouTube's script loads (the probe needs no network) and asks for a Referer.</summary>
    [Fact]
    public void Text_IsTheEmbeddedPage()
    {
        string page = VideoPage.Text();

        Assert.StartsWith("<!doctype html>", page, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"referrer\" content=\"strict-origin-when-cross-origin\">", page);
        int speaks = page.IndexOf("send({ ev: 'page' });", StringComparison.Ordinal);
        int api = page.IndexOf("<script src=\"https://www.youtube.com/iframe_api\"></script>", StringComparison.Ordinal);
        Assert.True(speaks > 0 && api > speaks, "the page message comes before the API script");
        Assert.Contains("chrome.webview.addEventListener('message'", page);
    }

    [Fact]
    public void Write_MakesTheFolder_AndLeavesAnEqualFileAlone()
    {
        string site = Path.Combine(_dir, "webview2", "site");

        string path = VideoPage.Write(site);

        Assert.Equal(Path.Combine(site, VideoPage.FileName), path);
        Assert.Equal(VideoPage.Text(), File.ReadAllText(path));
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        VideoPage.Write(site);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));   // the same text: not rewritten

        File.WriteAllText(path, "old page");
        VideoPage.Write(site);
        Assert.Equal(VideoPage.Text(), File.ReadAllText(path));
    }

    // ── WebViewHandler ───────────────────────────────────────────────────────

    [Fact]
    public void QueryInterface_AnswersIUnknownAndItsIid_AndCounts()
    {
        nint handler = WebViewHandler.Event(SomeIid, (_, _) => { });
        try
        {
            Assert.Equal(WebViewHandler.SOk, Query(handler, IidUnknown, out nint unknown));
            Assert.Equal(handler, unknown);
            Assert.Equal(WebViewHandler.SOk, Query(handler, SomeIid, out nint same));
            Assert.Equal(handler, same);
            Assert.Equal(WebViewHandler.ENoInterface, Query(handler, Guid.NewGuid(), out nint none));
            Assert.Equal(0, none);

            Assert.Equal(4u, AddRef(handler));    // 1 of the caller, 2 queries, this one
            Assert.Equal(3u, Release(handler));
            Assert.Equal(2u, Release(handler));
            Assert.Equal(1u, Release(handler));
        }
        finally
        {
            Assert.Equal(0u, WebViewHandler.Release(handler));
        }
    }

    [Fact]
    public void Event_GetsSenderAndArgs()
    {
        (nint, nint) seen = default;
        nint handler = WebViewHandler.Event(SomeIid, (sender, args) => seen = (sender, args));
        try
        {
            Assert.Equal(WebViewHandler.SOk, ((delegate* unmanaged<nint, nint, nint, int>)Slot(handler, 3))(handler, 11, 22));
            Assert.Equal((11, 22), seen);
        }
        finally
        {
            WebViewHandler.Release(handler);
        }
    }

    /// <summary>The HRESULT is 32 bits: a failure arrives negative and whole, the result pointer beside it untouched.</summary>
    [Fact]
    public void Completed_GetsTheHresultWhole_AndTheResult()
    {
        (int, nint) seen = default;
        nint handler = WebViewHandler.Completed(SomeIid, (hr, result) => seen = (hr, result));
        try
        {
            ((delegate* unmanaged<nint, int, nint, int>)Slot(handler, 3))(handler, unchecked((int)0x80070002), 0x1234);
            Assert.Equal((unchecked((int)0x80070002), (nint)0x1234), seen);
        }
        finally
        {
            WebViewHandler.Release(handler);
        }
    }

    [Fact]
    public void Script_CopiesTheJson_OrGivesNull()
    {
        var seen = new List<(int, string?)>();
        nint handler = WebViewHandler.Script(SomeIid, (hr, json) => seen.Add((hr, json)));
        try
        {
            var invoke = (delegate* unmanaged<nint, int, char*, int>)Slot(handler, 3);
            fixed (char* json = "\"player.neonsidekick.example\"")
            {
                invoke(handler, 0, json);
            }

            invoke(handler, -1, null);
            Assert.Equal([(0, "\"player.neonsidekick.example\""), (-1, null)], seen);
        }
        finally
        {
            WebViewHandler.Release(handler);
        }
    }

    /// <summary>Nothing may unwind into native code: a callback that throws is logged and answered S_OK.</summary>
    [Fact]
    public void AThrowingCallback_IsLogged_AndAnsweredSOk()
    {
        var errors = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Video" && e.Level == DiagnosticLevel.Error) errors.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        nint handler = WebViewHandler.Event(SomeIid, (_, _) => throw new InvalidOperationException("boom"));
        try
        {
            Assert.Equal(WebViewHandler.SOk, ((delegate* unmanaged<nint, nint, nint, int>)Slot(handler, 3))(handler, 0, 0));
            Assert.Equal(["A WebView2 event handler failed."], errors);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
            WebViewHandler.Release(handler);
        }
    }

    /// <summary>The last Release frees the handle: the callback can be collected once WebView2 lets go.</summary>
    [Fact]
    public void TheLastRelease_LetsTheCallbackGo()
    {
        var (handler, callback) = MakeHandler();
        Assert.Equal(2u, AddRef(handler));   // WebView2's reference
        Assert.Equal(1u, WebViewHandler.Release(handler));   // the caller's, after handing it over
        Collect();
        Assert.True(callback.IsAlive, "still held while WebView2 holds the object");

        Assert.Equal(0u, Release(handler));   // WebView2's, through the vtable
        Collect();
        Assert.False(callback.IsAlive);
    }

    // ── WebViewOptions ───────────────────────────────────────────────────────

    /// <summary>The loader's view of the options: the autoplay flag, the SDK's version, no language, no sign-on; newer interfaces refused.</summary>
    [Fact]
    public void Options_AnswerTheArguments_AndTheTargetVersion()
    {
        nint options = WebViewOptions.Create(WebViewOptions.BrowserArguments);
        try
        {
            Assert.Equal(WebViewHandler.SOk, Query(options, WebViewOptions.IidEnvironmentOptions, out nint same));
            Assert.Equal(options, same);
            Assert.Equal(1u, Release(options));
            Assert.Equal(WebViewHandler.ENoInterface, Query(options, new Guid("FF85C98A-1BA7-4A6B-90C8-2B752C89E9E2"), out _));   // EnvironmentOptions2

            Assert.Equal("--autoplay-policy=no-user-gesture-required", TakeString(options, 3));
            Assert.Null(TakeString(options, 5));
            Assert.Equal("154.0.4258.31", TakeString(options, 7));
            int signOn = -1;
            Assert.Equal(WebViewHandler.SOk, ((delegate* unmanaged<nint, int*, int>)Slot(options, 9))(options, &signOn));
            Assert.Equal(0, signOn);
            fixed (char* ignored = "en-GB")
            {
                Assert.Equal(WebViewHandler.SOk, ((delegate* unmanaged<nint, char*, int>)Slot(options, 6))(options, ignored));
            }

            Assert.Equal(WebViewHandler.SOk, ((delegate* unmanaged<nint, int, int>)Slot(options, 10))(options, 1));
            Assert.Equal("154.0.4258.31", TakeString(options, 7));   // the setters change nothing
        }
        finally
        {
            Assert.Equal(0u, WebViewOptions.Release(options));
        }
    }

    // ── The probe ────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole chain under the JIT, a hidden window and a temp profile: a real WebView2 where the runtime is installed
    /// (a few seconds, nothing shown, no network), skipped and passed where it is not.
    /// </summary>
    [Fact]
    public void Probe_RunsTheChain_OrSkipsWithoutARuntime()
    {
        var check = SmokeChecks.ProbeVideoWebView2();

        Assert.Equal("video:webview2", check.Name);
        Assert.True(check.Passed, check.Detail);
        if (!check.Detail.StartsWith("skipped: ", StringComparison.Ordinal))
        {
            Assert.Matches("^runtime \\d+\\.\\d+\\.\\d+\\.\\d+; the page on player\\.neonsidekick\\.example in \\d+ ms; web messages both ways, a script's answer, a navigation off the host refused$", check.Detail);
        }

        Assert.Contains("WebView2Loader.dll", SmokeChecks.RequiredNativeLibraries);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, VideoPage.LoaderFileName)), "the csproj copies the loader beside the binary");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (nint Handler, WeakReference Callback) MakeHandler()
    {
        var box = new object();
        Action<nint, nint> callback = (_, _) => GC.KeepAlive(box);
        return (WebViewHandler.Event(SomeIid, callback), new WeakReference(callback));
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static int Query(nint handler, Guid iid, out nint found)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(handler, 0))(handler, &iid, &result);
        found = result;
        return hr;
    }

    // A getter's string, freed as the loader frees it.
    private static string? TakeString(nint instance, int slot)
    {
        char* text = null;
        Assert.Equal(WebViewHandler.SOk, ((delegate* unmanaged<nint, char**, int>)Slot(instance, slot))(instance, &text));
        if (text is null)
        {
            return null;
        }

        string value = new(text);
        System.Runtime.InteropServices.Marshal.FreeCoTaskMem((nint)text);
        return value;
    }

    private static uint AddRef(nint handler) => ((delegate* unmanaged<nint, uint>)Slot(handler, 1))(handler);

    private static uint Release(nint handler) => ((delegate* unmanaged<nint, uint>)Slot(handler, 2))(handler);

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
}
